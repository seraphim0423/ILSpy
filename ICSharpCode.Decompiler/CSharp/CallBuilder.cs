﻿// Copyright (c) 2014 Daniel Grunwald
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.CSharp.TypeSystem;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;
using ICSharpCode.Decompiler.Util;
using ExpressionType = System.Linq.Expressions.ExpressionType;
using PrimitiveType = ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType;
using System.Threading;

namespace ICSharpCode.Decompiler.CSharp
{
	struct CallBuilder
	{
		readonly DecompilerSettings settings;
		readonly ExpressionBuilder expressionBuilder;
		readonly CSharpResolver resolver;
		readonly IDecompilerTypeSystem typeSystem;

		public CallBuilder(ExpressionBuilder expressionBuilder, IDecompilerTypeSystem typeSystem, DecompilerSettings settings)
		{
			this.expressionBuilder = expressionBuilder;
			this.resolver = expressionBuilder.resolver;
			this.settings = settings;
			this.typeSystem = typeSystem;
		}

		public TranslatedExpression Build(CallInstruction inst)
		{
			IMethod method = inst.Method;
			// Used for Call, CallVirt and NewObj
			TranslatedExpression target;
			if (inst.OpCode == OpCode.NewObj) {
				if (IL.Transforms.DelegateConstruction.IsDelegateConstruction((NewObj)inst, true)) {
					return HandleDelegateConstruction(inst);
				}
				target = default(TranslatedExpression); // no target
			} else {
				target = expressionBuilder.TranslateTarget(method, inst.Arguments.FirstOrDefault(), inst.OpCode == OpCode.Call);
			}

			int firstParamIndex = (method.IsStatic || inst.OpCode == OpCode.NewObj) ? 0 : 1;

			// Translate arguments to the expected parameter types
			var arguments = new List<TranslatedExpression>(method.Parameters.Count);
			Debug.Assert(inst.Arguments.Count == firstParamIndex + method.Parameters.Count);
			var expectedParameters = method.Parameters.ToList();
			bool isExpandedForm = false;
			for (int i = 0; i < method.Parameters.Count; i++) {
				var parameter = expectedParameters[i];
				var arg = expressionBuilder.Translate(inst.Arguments[firstParamIndex + i]);
				if (parameter.IsParams && i + 1 == method.Parameters.Count) {
					// Parameter is marked params
					// If the argument is an array creation, inline all elements into the call and add missing default values.
					// Otherwise handle it normally.
					if (arg.ResolveResult is ArrayCreateResolveResult acrr &&
						acrr.SizeArguments.Count == 1 &&
						acrr.SizeArguments[0].IsCompileTimeConstant &&
						acrr.SizeArguments[0].ConstantValue is int length) {
						var expandedParameters = expectedParameters.Take(expectedParameters.Count - 1).ToList();
						var expandedArguments = new List<TranslatedExpression>(arguments);
						if (length > 0) {
							var arrayElements = ((ArrayCreateExpression)arg.Expression).Initializer.Elements.ToArray();
							var elementType = ((ArrayType)acrr.Type).ElementType;
							for (int j = 0; j < length; j++) {
								expandedParameters.Add(new DefaultParameter(elementType, parameter.Name + j));
								if (j < arrayElements.Length)
									expandedArguments.Add(new TranslatedExpression(arrayElements[j]));
								else
									expandedArguments.Add(expressionBuilder.GetDefaultValueExpression(elementType).WithoutILInstruction());
							}
						}
						if (IsUnambiguousCall(inst, target, method, Array.Empty<IType>(), expandedArguments) == OverloadResolutionErrors.None) {
							isExpandedForm = true;
							expectedParameters = expandedParameters;
							arguments = expandedArguments.SelectList(a => new TranslatedExpression(a.Expression.Detach()));
							continue;
						}
					}
				}

				arguments.Add(arg.ConvertTo(parameter.Type, expressionBuilder, allowImplicitConversion: true));

				if (parameter.IsOut && arguments[i].Expression is DirectionExpression dirExpr) {
					dirExpr.FieldDirection = FieldDirection.Out;
				}
			}

			if (method is VarArgInstanceMethod) {
				int regularParameterCount = ((VarArgInstanceMethod)method).RegularParameterCount;
				var argListArg = new UndocumentedExpression();
				argListArg.UndocumentedExpressionType = UndocumentedExpressionType.ArgList;
				int paramIndex = regularParameterCount;
				var builder = expressionBuilder;
				argListArg.Arguments.AddRange(arguments.Skip(regularParameterCount).Select(arg => arg.ConvertTo(expectedParameters[paramIndex++].Type, builder).Expression));
				var argListRR = new ResolveResult(SpecialType.ArgList);
				arguments = arguments.Take(regularParameterCount)
					.Concat(new[] { argListArg.WithoutILInstruction().WithRR(argListRR) }).ToList();
				method = (IMethod)method.MemberDefinition;
				expectedParameters = method.Parameters.ToList();
			}

			var argumentResolveResults = arguments.Select(arg => arg.ResolveResult).ToList();

			ResolveResult rr = new CSharpInvocationResolveResult(target.ResolveResult, method, argumentResolveResults, isExpandedForm: isExpandedForm);

			if (inst.OpCode == OpCode.NewObj) {
				if (settings.AnonymousTypes && method.DeclaringType.IsAnonymousType()) {
					var argumentExpressions = arguments.SelectArray(arg => arg.Expression);
					AnonymousTypeCreateExpression atce = new AnonymousTypeCreateExpression();
					if (CanInferAnonymousTypePropertyNamesFromArguments(argumentExpressions, expectedParameters)) {
						atce.Initializers.AddRange(argumentExpressions);
					} else {
						for (int i = 0; i < argumentExpressions.Length; i++) {
							atce.Initializers.Add(
								new NamedExpression {
									Name = expectedParameters[i].Name,
									Expression = argumentExpressions[i]
								});
						}
					}
					return atce
						.WithILInstruction(inst)
						.WithRR(rr);
				} else {
					if (IsUnambiguousCall(inst, target, method, Array.Empty<IType>(), arguments) != OverloadResolutionErrors.None) {
						for (int i = 0; i < arguments.Count; i++) {
							if (!settings.AnonymousTypes || !expectedParameters[i].Type.ContainsAnonymousType())
								arguments[i] = arguments[i].ConvertTo(expectedParameters[i].Type, expressionBuilder);
						}
					}
					return new ObjectCreateExpression(expressionBuilder.ConvertType(inst.Method.DeclaringType), arguments.SelectArray(arg => arg.Expression))
						.WithILInstruction(inst).WithRR(rr);
				}
			} else {
				int allowedParamCount = (method.ReturnType.IsKnownType(KnownTypeCode.Void) ? 1 : 0);
				if (method.IsAccessor && (method.AccessorOwner.SymbolKind == SymbolKind.Indexer || expectedParameters.Count == allowedParamCount)) {
					return HandleAccessorCall(inst, target, method, arguments.ToList());
				} else {
					bool requireTypeArguments = false;
					bool targetCasted = false;
					bool argumentsCasted = false;
					IType[] typeArguments = Array.Empty<IType>();

					OverloadResolutionErrors errors;
					while ((errors = IsUnambiguousCall(inst, target, method, typeArguments, arguments)) != OverloadResolutionErrors.None) {
						switch (errors) {
							case OverloadResolutionErrors.TypeInferenceFailed:
							case OverloadResolutionErrors.WrongNumberOfTypeArguments:
								if (requireTypeArguments) goto default;
								requireTypeArguments = true;
								typeArguments = method.TypeArguments.ToArray();
								continue;
							default:
								if (!argumentsCasted) {
									argumentsCasted = true;
									for (int i = 0; i < arguments.Count; i++) {
										if (!settings.AnonymousTypes || !expectedParameters[i].Type.ContainsAnonymousType())
											arguments[i] = arguments[i].ConvertTo(expectedParameters[i].Type, expressionBuilder);
									}
								} else if (!targetCasted) {
									targetCasted = true;
									target = target.ConvertTo(method.DeclaringType, expressionBuilder);
								} else if (!requireTypeArguments) {
									requireTypeArguments = true;
									typeArguments = method.TypeArguments.ToArray();
								} else {
									break;
								}
								continue;
						}
						break;
					}

					Expression targetExpr = target.Expression;
					string methodName = method.Name;
					// HACK : convert this.Dispose() to ((IDisposable)this).Dispose(), if Dispose is an explicitly implemented interface method.
					if (inst.Method.IsExplicitInterfaceImplementation && targetExpr is ThisReferenceExpression) {
						targetExpr = new CastExpression(expressionBuilder.ConvertType(method.ImplementedInterfaceMembers[0].DeclaringType), targetExpr);
						methodName = method.ImplementedInterfaceMembers[0].Name;
					}
					var mre = new MemberReferenceExpression(targetExpr, methodName);
					if (requireTypeArguments && (!settings.AnonymousTypes || !method.TypeArguments.Any(a => a.ContainsAnonymousType())))
						mre.TypeArguments.AddRange(method.TypeArguments.Select(expressionBuilder.ConvertType));
					var argumentExpressions = arguments.Select(arg => arg.Expression);
					return new InvocationExpression(mre, argumentExpressions).WithILInstruction(inst).WithRR(rr);
				}
			}
		}

		OverloadResolutionErrors IsUnambiguousCall(ILInstruction inst, TranslatedExpression target, IMethod method, IType[] typeArguments, IList<TranslatedExpression> arguments)
		{
			var lookup = new MemberLookup(resolver.CurrentTypeDefinition, resolver.CurrentTypeDefinition.ParentAssembly);
			var or = new OverloadResolution(resolver.Compilation, arguments.SelectArray(a => a.ResolveResult), typeArguments: typeArguments);
			if (inst is NewObj newObj) {
				foreach (IMethod ctor in newObj.Method.DeclaringType.GetConstructors()) {
					if (lookup.IsAccessible(ctor, allowProtectedAccess: resolver.CurrentTypeDefinition == newObj.Method.DeclaringTypeDefinition)) {
						or.AddCandidate(ctor);
					}
				}
			} else {
				var result = lookup.Lookup(target.ResolveResult, method.Name, EmptyList<IType>.Instance, true) as MethodGroupResolveResult;
				if (result == null)
					return OverloadResolutionErrors.AmbiguousMatch;
				or.AddMethodLists(result.MethodsGroupedByDeclaringType.ToArray());
			}
			if (or.BestCandidateErrors != OverloadResolutionErrors.None)
				return or.BestCandidateErrors;
			if (!IsAppropriateCallTarget(method, or.GetBestCandidateWithSubstitutedTypeArguments(), inst.OpCode == OpCode.CallVirt))
				return OverloadResolutionErrors.AmbiguousMatch;
			return OverloadResolutionErrors.None;
		}

		static bool CanInferAnonymousTypePropertyNamesFromArguments(IList<Expression> args, IList<IParameter> parameters)
		{
			for (int i = 0; i < args.Count; i++) {
				string inferredName;
				if (args[i] is IdentifierExpression)
					inferredName = ((IdentifierExpression)args[i]).Identifier;
				else if (args[i] is MemberReferenceExpression)
					inferredName = ((MemberReferenceExpression)args[i]).MemberName;
				else
					inferredName = null;

				if (inferredName != parameters[i].Name) {
					return false;
				}
			}
			return true;
		}

		TranslatedExpression HandleAccessorCall(ILInstruction inst, TranslatedExpression target, IMethod method, IList<TranslatedExpression> arguments)
		{
			var lookup = new MemberLookup(resolver.CurrentTypeDefinition, resolver.CurrentTypeDefinition.ParentAssembly);
			var result = lookup.Lookup(target.ResolveResult, method.AccessorOwner.Name, EmptyList<IType>.Instance, isInvocation: false);

			if (result.IsError || (result is MemberResolveResult && !IsAppropriateCallTarget(method.AccessorOwner, ((MemberResolveResult)result).Member, inst.OpCode == OpCode.CallVirt)))
				target = target.ConvertTo(method.AccessorOwner.DeclaringType, expressionBuilder);
			var rr = new MemberResolveResult(target.ResolveResult, method.AccessorOwner);

			if (method.ReturnType.IsKnownType(KnownTypeCode.Void)) {
				var value = arguments.Last();
				arguments.Remove(value);
				TranslatedExpression expr;
				if (arguments.Count == 0)
					expr = new MemberReferenceExpression(target.Expression, method.AccessorOwner.Name)
						.WithoutILInstruction().WithRR(rr);
				else
					expr = new IndexerExpression(target.Expression, arguments.Select(a => a.Expression))
						.WithoutILInstruction().WithRR(rr);
				var op = AssignmentOperatorType.Assign;
				var parentEvent = method.AccessorOwner as IEvent;
				if (parentEvent != null) {
					if (method.Equals(parentEvent.AddAccessor)) {
						op = AssignmentOperatorType.Add;
					}
					if (method.Equals(parentEvent.RemoveAccessor)) {
						op = AssignmentOperatorType.Subtract;
					}
				}
				return new AssignmentExpression(expr, op, value.Expression).WithILInstruction(inst).WithRR(new TypeResolveResult(method.AccessorOwner.ReturnType));
			} else {
				if (arguments.Count == 0)
					return new MemberReferenceExpression(target.Expression, method.AccessorOwner.Name).WithILInstruction(inst).WithRR(rr);
				else
					return new IndexerExpression(target.Expression, arguments.Select(a => a.Expression)).WithILInstruction(inst).WithRR(rr);
			}
		}

		bool IsAppropriateCallTarget(IMember expectedTarget, IMember actualTarget, bool isVirtCall)
		{
			if (expectedTarget.Equals(actualTarget))
				return true;

			if (isVirtCall && actualTarget.IsOverride) {
				foreach (var possibleTarget in InheritanceHelper.GetBaseMembers(actualTarget, false)) {
					if (expectedTarget.Equals(possibleTarget))
						return true;
					if (!possibleTarget.IsOverride)
						break;
				}
			}
			return false;
		}

		TranslatedExpression HandleDelegateConstruction(CallInstruction inst)
		{
			ILInstruction func = inst.Arguments[1];
			IMethod method;
			switch (func.OpCode) {
				case OpCode.LdFtn:
					method = ((LdFtn)func).Method;
					break;
				case OpCode.LdVirtFtn:
					method = ((LdVirtFtn)func).Method;
					break;
				default:
					method = (IMethod)typeSystem.Resolve(((ILFunction)func).Method);
					break;
			}
			var target = expressionBuilder.TranslateTarget(method, inst.Arguments[0], func.OpCode == OpCode.LdFtn);
			var lookup = new MemberLookup(resolver.CurrentTypeDefinition, resolver.CurrentTypeDefinition.ParentAssembly);
			var or = new OverloadResolution(resolver.Compilation, method.Parameters.SelectArray(p => new TypeResolveResult(p.Type)));
			var result = lookup.Lookup(target.ResolveResult, method.Name, method.TypeArguments, true) as MethodGroupResolveResult;

			if (result == null) {
				target = target.ConvertTo(method.DeclaringType, expressionBuilder);
			} else {
				or.AddMethodLists(result.MethodsGroupedByDeclaringType.ToArray());
				if (or.BestCandidateErrors != OverloadResolutionErrors.None || !IsAppropriateCallTarget(method, or.BestCandidate, func.OpCode == OpCode.LdVirtFtn))
					target = target.ConvertTo(method.DeclaringType, expressionBuilder);
			}

			var mre = new MemberReferenceExpression(target, method.Name);
			mre.TypeArguments.AddRange(method.TypeArguments.Select(expressionBuilder.ConvertType));
			var oce = new ObjectCreateExpression(expressionBuilder.ConvertType(inst.Method.DeclaringType), mre)
				//				.WithAnnotation(new DelegateConstruction.Annotation(func.OpCode == OpCode.LdVirtFtn, target, method.Name))
				.WithILInstruction(inst)
				.WithRR(new ConversionResolveResult(
					inst.Method.DeclaringType,
					new MemberResolveResult(target.ResolveResult, method),
					// TODO handle extension methods capturing the first argument
					Conversion.MethodGroupConversion(method, func.OpCode == OpCode.LdVirtFtn, false)));

			if (func is ILFunction) {
				return expressionBuilder.TranslateFunction(oce, target, (ILFunction)func);
			} else {
				return oce;
			}
		}
	}
}