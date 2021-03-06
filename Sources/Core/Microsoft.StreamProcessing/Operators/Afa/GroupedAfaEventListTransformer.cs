﻿// *********************************************************************
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License
// *********************************************************************
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reflection;
using Microsoft.StreamProcessing.Internal;

namespace Microsoft.StreamProcessing
{
    internal partial class GroupedAfaEventListTemplate : AfaTemplate
    {
        private Func<string, string, string> keyEqualityComparer;

        private GroupedAfaEventListTemplate(string className, Type keyType, Type payloadType, Type registerType, Type accumulatorType)
            : base(className, keyType, payloadType, registerType, accumulatorType)
        {
            if (Config.ForceRowBasedExecution)
            {
                this.sourceBatchTypeName = string.Format("Microsoft.StreamProcessing.StreamMessage<{0}, {1}>", this.TKey, this.TPayload);
                this.resultBatchTypeName = string.Format("Microsoft.StreamProcessing.StreamMessage<{0}, {1}>", this.TKey, this.TRegister);
            }
            else
            {
                this.sourceBatchTypeName = Transformer.GetBatchClassName(keyType, payloadType);
                this.resultBatchTypeName = Transformer.GetBatchClassName(keyType, registerType);
            }
        }

        internal static Tuple<Type, string> GenerateAFA<TKey, TPayload, TRegister, TAccumulator>(
            AfaStreamable<TKey, TPayload, TRegister, TAccumulator> stream)
        {
            Contract.Requires(stream != null);
            Contract.Ensures(Contract.Result<Tuple<Type, string>>() == null || typeof(UnaryPipe<TKey, TPayload, TRegister>).GetTypeInfo().IsAssignableFrom(Contract.Result<Tuple<Type, string>>().Item1));

            string errorMessages = null;
            try
            {
                var className = string.Format("GeneratedGroupedAfaEventList_{0}", AFASequenceNumber++);
                var template = new GroupedAfaEventListTemplate(className, typeof(TKey), typeof(TPayload), typeof(TRegister), typeof(TAccumulator))
                {
                    TKey = typeof(TKey).GetCSharpSourceSyntax(),
                    isFinal = stream.afa.isFinal,
                    hasOutgoingArcs = stream.afa.hasOutgoingArcs,
                    startStates = stream.afa.startStates,
                    AllowOverlappingInstances = stream.afa.uncompiledAfa.AllowOverlappingInstances
                };

                var d1 = stream.afa.uncompiledAfa.transitionInfo;
                var orderedKeys = d1.Keys.OrderBy(e => e).ToArray();
                for (int i = 0; i < orderedKeys.Length; i++)
                {
                    var sourceNodeNumber = orderedKeys[i];
                    var outgoingEdgesDictionary = d1[sourceNodeNumber];
                    var orderedTargetNodes = outgoingEdgesDictionary.Keys.OrderBy(e => e).ToArray();
                    var edgeList1 = new List<EdgeInfo>();
                    for (int j = 0; j < orderedTargetNodes.Length; j++)
                    {
                        var targetNodeNumber = orderedTargetNodes[j];
                        var edge = outgoingEdgesDictionary[targetNodeNumber];
                        if (edge is ListElementArc<TPayload, TRegister> leArc)
                        {
                            var edgeInfo = new EdgeInfo()
                            {
                                Type = EdgeInfo.EdgeType.List,
                                EpsilonReachableNodes = EpsilonClosure(stream.afa, targetNodeNumber),
                                Fence = (ts, evs, reg) => leArc.Fence.Inline(ts, evs, reg),
                                Transfer = leArc.Transfer == null ? ((Func<string, string, string, string>)null) : (ts, evs, reg) => leArc.Transfer.Inline(ts, evs, reg),
                            };
                            edgeList1.Add(edgeInfo);
                        }

                    }
                    template.currentlyActiveInfo.Add(Tuple.Create(sourceNodeNumber, edgeList1));
                }
                for (int i = 0; i < stream.afa.startStates.Length; i++)
                {
                    var startState = stream.afa.startStates[i];
                    var edgeList2 = new List<EdgeInfo>();
                    var outgoingEdgeDictionary = stream.afa.uncompiledAfa.transitionInfo[startState];
                    foreach (var edge in outgoingEdgeDictionary)
                    {
                        var targetNode = edge.Key;
                        var arc = edge.Value;
                        if (arc is ListElementArc<TPayload, TRegister> leArc)
                        {
                            var edgeInfo = new EdgeInfo()
                            {
                                Type = EdgeInfo.EdgeType.List,
                                EpsilonReachableNodes = EpsilonClosure(stream.afa, targetNode),
                                Fence = (ts, evs, reg) => leArc.Fence.Inline(ts, evs, reg),
                                Transfer = leArc.Transfer == null ? ((Func<string, string, string, string>)null) : (ts, evs, reg) => leArc.Transfer.Inline(ts, evs, reg),
                            };
                            edgeList2.Add(edgeInfo);
                        }
                    }

                    template.newActivationInfo.Add(Tuple.Create(startState, edgeList2));
                }
                template.isSyncTimeSimultaneityFree = true; // The handwritten version doesn't make a distinction.
                template.keyEqualityComparer =
                    (left, right) =>
                        stream.Properties.KeyEqualityComparer.GetEqualsExpr().Inline(left, right);

                var expandedCode = template.TransformText();

                var assemblyReferences = Transformer.AssemblyReferencesNeededFor(typeof(TKey), typeof(TPayload), typeof(TRegister), typeof(FastDictionaryGenerator));
                assemblyReferences.Add(typeof(IStreamable<,>).GetTypeInfo().Assembly);
                assemblyReferences.Add(Transformer.GeneratedStreamMessageAssembly<TKey, TPayload>());
                assemblyReferences.Add(Transformer.GeneratedStreamMessageAssembly<TKey, TRegister>());
                assemblyReferences.Add(Transformer.GeneratedMemoryPoolAssembly<TKey, TRegister>());

                var a = Transformer.CompileSourceCode(expandedCode, assemblyReferences, out errorMessages);
                var t = a.GetType(template.className);
                if (t.GetTypeInfo().IsGenericType)
                {
                    var list = typeof(TKey).GetAnonymousTypes();
                    list.AddRange(template.payloadType.GetAnonymousTypes());
                    list.AddRange(template.registerType.GetAnonymousTypes());
                    t = t.MakeGenericType(list.ToArray());
                }
                return Tuple.Create(t, errorMessages);
            }
            catch
            {
                if (Config.CodegenOptions.DontFallBackToRowBasedExecution)
                {
                    throw new InvalidOperationException("Code Generation failed when it wasn't supposed to!");
                }
                return Tuple.Create((Type)null, errorMessages);
            }
        }

    }

}
