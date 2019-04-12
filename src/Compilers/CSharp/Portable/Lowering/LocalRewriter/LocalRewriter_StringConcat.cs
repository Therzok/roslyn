﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;
using Microsoft.CodeAnalysis.RuntimeMembers;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {
        /// <summary>
        /// The strategy of this rewrite is to do rewrite "locally".
        /// We analyze arguments of the concat in a shallow fashion assuming that 
        /// lowering and optimizations (including this one) is already done for the arguments.
        /// Based on the arguments we select the most appropriate pattern for the current node.
        /// 
        /// NOTE: it is not guaranteed that the node that we chose will be the most optimal since we have only 
        ///       local information - i.e. we look at the arguments, but we do not know about siblings.
        ///       When we move to the parent, the node may be rewritten by this or some another optimization.
        ///       
        /// Example:
        ///     result = ( "abc" + "def" + null ?? expr1 + "moo" + "baz" ) + expr2
        /// 
        /// Will rewrite into:
        ///     result = Concat("abcdef", expr2)
        ///     
        /// However there will be transient nodes like  Concat(expr1 + "moo")  that will not be present in the
        /// resulting tree.
        ///
        /// </summary>
        private BoundExpression RewriteStringConcatenation(SyntaxNode syntax, BinaryOperatorKind operatorKind, BoundExpression loweredLeft, BoundExpression loweredRight, TypeSymbol type)
        {
            Debug.Assert(
                operatorKind == BinaryOperatorKind.StringConcatenation ||
                operatorKind == BinaryOperatorKind.StringAndObjectConcatenation ||
                operatorKind == BinaryOperatorKind.ObjectAndStringConcatenation);

            if (_inExpressionLambda)
            {
                return RewriteStringConcatInExpressionLambda(syntax, operatorKind, loweredLeft, loweredRight, type);
            }

            // Convert both sides to a string (calling ToString if necessary)
            loweredLeft = ConvertConcatExprToString(syntax, loweredLeft);
            loweredRight = ConvertConcatExprToString(syntax, loweredRight);

            Debug.Assert(loweredLeft.Type.IsStringType() || loweredLeft.ConstantValue?.IsNull == true || loweredLeft.Type.IsErrorType());
            Debug.Assert(loweredRight.Type.IsStringType() || loweredRight.ConstantValue?.IsNull == true || loweredRight.Type.IsErrorType());

            // try fold two args without flattening.
            var folded = TryFoldTwoConcatOperands(syntax, loweredLeft, loweredRight);
            if (folded != null)
            {
                return folded;
            }

            // flatten and merge -  ( expr1 + "A" ) + ("B" + expr2) ===> (expr1 + "AB" + expr2)
            ArrayBuilder<BoundExpression> leftFlattened = ArrayBuilder<BoundExpression>.GetInstance();
            ArrayBuilder<BoundExpression> rightFlattened = ArrayBuilder<BoundExpression>.GetInstance();

            FlattenConcatArg(loweredLeft, leftFlattened);
            FlattenConcatArg(loweredRight, rightFlattened);

            if (leftFlattened.Any() && rightFlattened.Any())
            {
                folded = TryFoldTwoConcatOperands(syntax, leftFlattened.Last(), rightFlattened.First());
                if (folded != null)
                {
                    rightFlattened[0] = folded;
                    leftFlattened.RemoveLast();
                }
            }

            leftFlattened.AddRange(rightFlattened);
            rightFlattened.Free();

            BoundExpression result;

            switch (leftFlattened.Count)
            {
                case 0:
                    result = _factory.StringLiteral(string.Empty);
                    break;

                case 1:
                    result = leftFlattened[0];
                    break;

                case 2:
                    var left = leftFlattened[0];
                    var right = leftFlattened[1];
                    result = RewriteStringConcatenationTwoExprs(syntax, left, right);
                    break;

                case 3:
                    var first = leftFlattened[0];
                    var second = leftFlattened[1];
                    var third = leftFlattened[2];
                    result = RewriteStringConcatenationThreeExprs(syntax, first, second, third);
                    break;

                default:
                    result = RewriteStringConcatenationManyExprs(syntax, leftFlattened.ToImmutable());
                    break;
            }

            leftFlattened.Free();
            return result;
        }

        /// <summary>
        /// digs into known concat operators and unwraps their arguments
        /// otherwise returns the expression as-is
        /// 
        /// Generally we only need to recognize same node patterns that we create as a result of concatenation rewrite.
        /// </summary>
        private void FlattenConcatArg(BoundExpression lowered, ArrayBuilder<BoundExpression> flattened)
        {
            if (TryExtractStringConcatArgs(lowered, out var arguments, out _))
            {
                flattened.AddRange(arguments);
            }
            else
            {
                // fallback - if nothing above worked, leave arg as-is
                flattened.Add(lowered);
            }
        }

        /// <summary>
        /// Determines whether an expression is a known string concat operator (with or without a subsequent ?? ""), and extracts
        /// its args if so.
        /// </summary>
        /// <param name="loweredCanReturnNull">
        /// True if this method returns true, and the expression can return null (string.Concat(object) can return null if
        /// the object's ToString method returns null)
        /// </param>
        /// <returns>True if this is a call to a known string concat operator, false otherwise</returns>
        private bool TryExtractStringConcatArgs(BoundExpression lowered, out ImmutableArray<BoundExpression> arguments, out bool loweredCanReturnNull)
        {
            switch (lowered.Kind)
            {
                case BoundKind.Call:
                    return TryExtractStringConcatArgsFromBoundCall((BoundCall)lowered, out arguments, out loweredCanReturnNull);

                case BoundKind.NullCoalescingOperator:
                    var boundCoalesce = (BoundNullCoalescingOperator)lowered;

                    if (boundCoalesce.LeftConversion.IsIdentity)
                    {
                        // The RHS may be a constant value with an identity conversion to string even
                        // if it is not a string: in particular, the null literal behaves this way.
                        // To be safe, check that the constant value is actually a string before
                        // attempting to access its value as a string.

                        var rightConstant = boundCoalesce.RightOperand.ConstantValue;
                        if (rightConstant != null && rightConstant.IsString && rightConstant.StringValue.Length == 0)
                        {
                            // If lowered ends in '?? ""', it can never return null.
                            loweredCanReturnNull = false;

                            // The left operand might be a call to string.Concat(object)
                            if (boundCoalesce.LeftOperand.Kind == BoundKind.Call &&
                                TryExtractStringConcatArgsFromBoundCall((BoundCall)boundCoalesce.LeftOperand, out arguments, out _))
                            {
                                return true;
                            }

                            arguments = ImmutableArray.Create(boundCoalesce.LeftOperand);
                            return true;
                        }
                    }
                    break;
            }

            arguments = default;
            loweredCanReturnNull = default;
            return false;
        }

        /// <summary>
        /// Attempts to extract the args from a call to string.Concat
        /// </summary>
        /// <param name="callCanReturnNull">
        /// True if this method returns true, and boundCall is a call to a string.Concat overload which can return null
        /// (i.e. string.Concat(object))
        /// </param>
        /// <returns>True if this was a call to string.Concat, false otherwise</returns>
        private bool TryExtractStringConcatArgsFromBoundCall(BoundCall boundCall, out ImmutableArray<BoundExpression> arguments, out bool callCanReturnNull)
        {
            var method = boundCall.Method;
            if (method.IsStatic && method.ContainingType.SpecialType == SpecialType.System_String)
            {
                if ((object)method == (object)_compilation.GetSpecialTypeMember(SpecialMember.System_String__ConcatObject))
                {
                    callCanReturnNull = true; // string.Concat(object) can return null
                    arguments = boundCall.Arguments;
                    return true;
                }

                callCanReturnNull = false; // other string.Concat overloads cannot return null

                if ((object)method == (object)_compilation.GetSpecialTypeMember(SpecialMember.System_String__ConcatStringString) ||
                    (object)method == (object)_compilation.GetSpecialTypeMember(SpecialMember.System_String__ConcatStringStringString) ||
                    (object)method == (object)_compilation.GetSpecialTypeMember(SpecialMember.System_String__ConcatStringStringStringString) ||
                    (object)method == (object)_compilation.GetSpecialTypeMember(SpecialMember.System_String__ConcatObjectObject) ||
                    (object)method == (object)_compilation.GetSpecialTypeMember(SpecialMember.System_String__ConcatObjectObjectObject))
                {
                    arguments = boundCall.Arguments;
                    return true;
                }

                if ((object)method == (object)_compilation.GetSpecialTypeMember(SpecialMember.System_String__ConcatStringArray) ||
                    (object)method == (object)_compilation.GetSpecialTypeMember(SpecialMember.System_String__ConcatObjectArray))
                {
                    var args = boundCall.Arguments[0] as BoundArrayCreation;
                    if (args != null)
                    {
                        var initializer = args.InitializerOpt;
                        if (initializer != null)
                        {
                            arguments = initializer.Initializers;
                            return true;
                        }
                    }
                }
            }

            arguments = default;
            callCanReturnNull = default;
            return false;
        }

        /// <summary>
        /// folds two concat operands into one expression if possible
        /// otherwise returns null
        /// </summary>
        private BoundExpression TryFoldTwoConcatOperands(SyntaxNode syntax, BoundExpression loweredLeft, BoundExpression loweredRight)
        {
            // both left and right are constants
            var leftConst = loweredLeft.ConstantValue;
            var rightConst = loweredRight.ConstantValue;

            if (leftConst != null && rightConst != null)
            {
                // const concat may fail to fold if strings are huge. 
                // This would be unusual.
                ConstantValue concatenated = TryFoldTwoConcatConsts(leftConst, rightConst);
                if (concatenated != null)
                {
                    return _factory.StringLiteral(concatenated);
                }
            }

            // one or another is null. 
            if (IsNullOrEmptyStringConstant(loweredLeft))
            {
                if (IsNullOrEmptyStringConstant(loweredRight))
                {
                    return _factory.Literal((string)null + (string)null);
                }

                return RewriteStringConcatenationOneExpr(syntax, loweredRight);
            }
            else if (IsNullOrEmptyStringConstant(loweredRight))
            {
                return RewriteStringConcatenationOneExpr(syntax, loweredLeft);
            }

            return null;
        }

        private static bool IsNullOrEmptyStringConstant(BoundExpression operand)
        {
            return (operand.ConstantValue != null && string.IsNullOrEmpty(operand.ConstantValue.StringValue)) ||
                    operand.IsDefaultValue();
        }

        /// <summary>
        /// folds two concat constants into one if possible
        /// otherwise returns null.
        /// It is generally always possible to concat constants, unless resulting string would be too large.
        /// </summary>
        private static ConstantValue TryFoldTwoConcatConsts(ConstantValue leftConst, ConstantValue rightConst)
        {
            var leftVal = leftConst.StringValue;
            var rightVal = rightConst.StringValue;

            if (!leftConst.IsDefaultValue && !rightConst.IsDefaultValue)
            {
                if (leftVal.Length + rightVal.Length < 0)
                {
                    return null;
                }
            }

            // TODO: if transient string allocations are an issue, consider introducing constants that contain builders.
            //       it may be not so easy to even get here though, since typical
            //       "A" + "B" + "C" + ... cases should be folded in the binder as spec requires so.
            //       we would be mostly picking here edge cases like "A" + (object)null + "B" + (object)null + ...
            return ConstantValue.Create(leftVal + rightVal);
        }

        /// <summary>
        /// Strangely enough there is such a thing as unary concatenation and it must be rewritten.
        /// </summary>
        private BoundExpression RewriteStringConcatenationOneExpr(SyntaxNode syntax, BoundExpression loweredOperand)
        {
            if (loweredOperand.Type.SpecialType == SpecialType.System_String)
            {
                // If it's a call to 'string.Concat(object) ?? ""' or another overload of 'string.Concat', we know the result cannot
                // be null. Otherwise if it's 'string.Concat(object)' or something which isn't 'string.Concat', return loweredOperand ?? ""
                if (TryExtractStringConcatArgs(loweredOperand, out _, out bool loweredCanReturnNull) && !loweredCanReturnNull)
                {
                    return loweredOperand;
                }
                else
                {
                    return _factory.Coalesce(loweredOperand, _factory.Literal(""));
                }
            }

            var method = UnsafeGetSpecialTypeMethod(syntax, SpecialMember.System_String__ConcatObject);
            Debug.Assert((object)method != null);

            // string.Concat(object) might return null (if the object's ToString method returns null): convert to ""
            return _factory.Coalesce((BoundExpression)BoundCall.Synthesized(syntax, null, method, loweredOperand), _factory.Literal(""));
        }

        private BoundExpression RewriteStringConcatenationTwoExprs(SyntaxNode syntax, BoundExpression loweredLeft, BoundExpression loweredRight)
        {
            SpecialMember member = (loweredLeft.Type.SpecialType == SpecialType.System_String && loweredRight.Type.SpecialType == SpecialType.System_String) ?
                SpecialMember.System_String__ConcatStringString :
                SpecialMember.System_String__ConcatObjectObject;

            var method = UnsafeGetSpecialTypeMethod(syntax, member);
            Debug.Assert((object)method != null);

            return (BoundExpression)BoundCall.Synthesized(syntax, null, method, loweredLeft, loweredRight);
        }

        private BoundExpression RewriteStringConcatenationThreeExprs(SyntaxNode syntax, BoundExpression loweredFirst, BoundExpression loweredSecond, BoundExpression loweredThird)
        {
            SpecialMember member = (loweredFirst.Type.SpecialType == SpecialType.System_String &&
                                    loweredSecond.Type.SpecialType == SpecialType.System_String &&
                                    loweredThird.Type.SpecialType == SpecialType.System_String) ?
                SpecialMember.System_String__ConcatStringStringString :
                SpecialMember.System_String__ConcatObjectObjectObject;

            var method = UnsafeGetSpecialTypeMethod(syntax, member);
            Debug.Assert((object)method != null);

            return BoundCall.Synthesized(syntax, null, method, ImmutableArray.Create(loweredFirst, loweredSecond, loweredThird));
        }

        private BoundExpression RewriteStringConcatenationManyExprs(SyntaxNode syntax, ImmutableArray<BoundExpression> loweredArgs)
        {
            Debug.Assert(loweredArgs.Length > 3);
            Debug.Assert(loweredArgs.All(a => a.HasErrors || a.Type.SpecialType == SpecialType.System_Object || a.Type.SpecialType == SpecialType.System_String));

            bool isObject = false;
            TypeSymbol elementType = null;

            foreach (var arg in loweredArgs)
            {
                elementType = arg.Type;
                if (elementType.SpecialType != SpecialType.System_String)
                {
                    isObject = true;
                    break;
                }
            }

            // Count == 4 is handled differently because there is a Concat method with 4 arguments
            // for strings, but there is no such method for objects.
            if (!isObject && loweredArgs.Length == 4)
            {
                SpecialMember member = SpecialMember.System_String__ConcatStringStringStringString;
                var method = UnsafeGetSpecialTypeMethod(syntax, member);
                Debug.Assert((object)method != null);

                return (BoundExpression)BoundCall.Synthesized(syntax, null, method, loweredArgs);
            }
            else
            {
                SpecialMember member = isObject ?
                    SpecialMember.System_String__ConcatObjectArray :
                    SpecialMember.System_String__ConcatStringArray;

                var method = UnsafeGetSpecialTypeMethod(syntax, member);
                Debug.Assert((object)method != null);

                var array = _factory.ArrayOrEmpty(elementType, loweredArgs);

                return (BoundExpression)BoundCall.Synthesized(syntax, null, method, array);
            }
        }

        /// <summary>
        /// Most of the above optimizations are not applicable in expression trees as the operator
        /// must stay a binary operator. We cannot do much beyond constant folding which is done in binder.
        /// </summary>
        private BoundExpression RewriteStringConcatInExpressionLambda(SyntaxNode syntax, BinaryOperatorKind operatorKind, BoundExpression loweredLeft, BoundExpression loweredRight, TypeSymbol type)
        {
            SpecialMember member = (operatorKind == BinaryOperatorKind.StringConcatenation) ?
                SpecialMember.System_String__ConcatStringString :
                SpecialMember.System_String__ConcatObjectObject;

            var method = UnsafeGetSpecialTypeMethod(syntax, member);
            Debug.Assert((object)method != null);

            return new BoundBinaryOperator(syntax, operatorKind, default(ConstantValue), method, default(LookupResultKind), loweredLeft, loweredRight, type);
        }

        /// <summary>
        /// Returns an expression which converts the given expression into a string (or null).
        /// If necessary, this invokes .ToString() on the expression, to avoid boxing value types.
        /// </summary>
        private BoundExpression ConvertConcatExprToString(SyntaxNode syntax, BoundExpression expr)
        {
            // If it's a value type, it'll have been boxed by the +(string, object) or +(object, string)
            // operator. Undo that.
            if (expr.Kind == BoundKind.Conversion)
            {
                BoundConversion conv = (BoundConversion)expr;
                if (conv.ConversionKind == ConversionKind.Boxing)
                {
                    expr = conv.Operand;
                }
            }

            // Is the expression a literal char?  If so, we can
            // simply make it a literal string instead and avoid any 
            // allocations for converting the char to a string at run time.
            // Similarly if it's a literal null, don't do anything special.
            if (expr.Kind == BoundKind.Literal)
            {
                ConstantValue cv = ((BoundLiteral)expr).ConstantValue;
                if (cv != null)
                {
                    if (cv.SpecialType == SpecialType.System_Char)
                    {
                        return _factory.StringLiteral(cv.CharValue.ToString());
                    }
                    else if (cv.IsNull)
                    {
                        return expr;
                    }
                }
            }

            // If it's a string already, just return it
            if (expr.Type.SpecialType == SpecialType.System_String)
            {
                return expr;
            }

            // Evaluate toString at the last possible moment, to avoid spurious diagnostics if it's missing.
            // All code paths below here use it.
            var toString = UnsafeGetSpecialTypeMethod(syntax, SpecialMember.System_Object__ToString);

            // If it's a special value type, we know that it has its own ToString method. Assume that this won't
            // be removed, and emit a direct call rather than a constrained virtual call. 
            // This keeps in the spirit of #7079, but expands the range of types to all special value types.
            if (expr.Type.IsValueType && expr.Type.SpecialType != SpecialType.None)
            {
                var type = (NamedTypeSymbol)expr.Type;
                var toStringMembers = type.GetMembers(toString.Name);
                foreach (var member in toStringMembers)
                {
                    var toStringMethod = (MethodSymbol)member;
                    if (toStringMethod.GetLeastOverriddenMethod(type) == (object)toString)
                    {
                        return BoundCall.Synthesized(expr.Syntax, expr, toStringMethod);
                    }
                }
            }

            // If it's a value type (or unconstrained generic), and it's not constant and not readonly,
            // then we need a copy. This is to mimic the old behaviour, where ToString was called on a box
            // of the value type, and so any side-effects of ToString weren't made to the original.
            if (!expr.Type.IsReferenceType && !expr.Type.IsReadOnly && expr.ConstantValue == null)
            {
                expr = new BoundPassByCopy(expr.Syntax, expr, expr.Type);
            }

            // No need for a conditional access if it's a value type - we know it's not null.
            if (expr.Type.IsValueType)
            {
                return BoundCall.Synthesized(expr.Syntax, expr, toString);
            }

            int currentConditionalAccessID = ++_currentConditionalAccessID;

            return new BoundLoweredConditionalAccess(
                syntax,
                expr,
                hasValueMethodOpt: null,
                whenNotNull: BoundCall.Synthesized(
                    syntax,
                    new BoundConditionalReceiver(syntax, currentConditionalAccessID, expr.Type),
                    toString),
                whenNullOpt: null,
                id: currentConditionalAccessID,
                type: _compilation.GetSpecialType(SpecialType.System_String));
        }
    }
}
