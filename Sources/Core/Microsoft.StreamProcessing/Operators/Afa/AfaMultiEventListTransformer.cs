﻿// *********************************************************************
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License
// *********************************************************************
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reflection;

namespace Microsoft.StreamProcessing
{
    internal partial class AfaMultiEventListTemplate : AfaTemplate
    {
        private Func<string, string, string> keyEqualityComparer;
        protected readonly List<Tuple<int, List<EdgeInfo>>> edgeInfos = new List<Tuple<int, List<EdgeInfo>>>();
        protected readonly List<Tuple<int, List<EdgeInfo>>> startEdgeInfos = new List<Tuple<int, List<EdgeInfo>>>();
        private bool payloadIsAnon;
        private bool payloadHasNoFields;

        private AfaMultiEventListTemplate(string className, Type keyType, Type payloadType, Type registerType, Type accumulatorType)
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

            this.TAccumulator = accumulatorType.GetCSharpSourceSyntax();
        }

        internal static Tuple<Type, string> GenerateAFA<TKey, TPayload, TRegister, TAccumulator>(
            AfaStreamable<TKey, TPayload, TRegister, TAccumulator> stream)
        {
            Contract.Requires(stream != null);
            Contract.Ensures(Contract.Result<Tuple<Type, string>>() == null || typeof(UnaryPipe<TKey, TPayload, TRegister>).GetTypeInfo().IsAssignableFrom(Contract.Result<Tuple<Type, string>>().Item1));

            string errorMessages = null;
            try
            {
                var className = string.Format("Generated{1}AfaMultiEventList_{0}", AFASequenceNumber++, typeof(TKey).Equals(typeof(Empty)) ? string.Empty : "Grouped");
                var template = new AfaMultiEventListTemplate(className, typeof(TKey), typeof(TPayload), typeof(TRegister), typeof(TAccumulator))
                {
                    TKey = typeof(TKey).GetCSharpSourceSyntax()
                };
                var payloadRepresentation =
                    Config.ForceRowBasedExecution
                    ?
                    new ColumnarRepresentation(typeof(TPayload))
                    :
                    new ColumnarRepresentation(typeof(TPayload))
                    ;

                template.isFinal = stream.afa.isFinal;
                template.hasOutgoingArcs = stream.afa.hasOutgoingArcs;
                template.startStates = stream.afa.startStates;
                template.AllowOverlappingInstances = stream.afa.uncompiledAfa.AllowOverlappingInstances;
                template.payloadIsAnon = typeof(TPayload).IsAnonymousTypeName();
                template.payloadHasNoFields = payloadRepresentation.noFields;

                var d1 = stream.afa.uncompiledAfa.transitionInfo;
                var orderedKeys = d1.Keys.OrderBy(e => e).ToArray();
                for (int i = 0; i < orderedKeys.Length; i++)
                {
                    var sourceNodeNumber = orderedKeys[i];
                    var outgoingEdgesDictionary = d1[sourceNodeNumber];
                    var orderedTargetNodes = outgoingEdgesDictionary.Keys.OrderBy(e => e).ToArray();
                    var edgeList = new List<EdgeInfo>();
                    for (int j = 0; j < orderedTargetNodes.Length; j++)
                    {
                        var targetNodeNumber = orderedTargetNodes[j];
                        var edge = outgoingEdgesDictionary[targetNodeNumber];
                        if (edge is MultiElementArc<TPayload, TRegister, TAccumulator> multiArc)
                        {
                            var multiEdgeInfo = new MultiEdgeInfo()
                            {
                                SourceNode = sourceNodeNumber,
                                TargetNode = targetNodeNumber,
                                fromStartState = stream.afa.startStates.Any(n => n == sourceNodeNumber),
                                EpsilonReachableNodes = EpsilonClosure(stream.afa, targetNodeNumber),
                                Initialize = (ts, reg) => multiArc.Initialize.Inline(ts, reg),
                                Accumulate = (ts, ev, reg, acc) => multiArc.Accumulate.Inline(ts, ev, reg, acc),
                                Fence = (ts, acc, reg) => multiArc.Fence.Inline(ts, acc, reg),
                            };
                            if (multiArc.Transfer == null)
                            {
                                multiEdgeInfo.Transfer = null;
                            }
                            else
                            {
                                multiEdgeInfo.Transfer = (ts, acc, reg) => multiArc.Transfer.Inline(ts, acc, reg);
                            }
                            if (multiArc.Dispose == null)
                            {
                                multiEdgeInfo.Dispose = (acc) => "// no dispose function";
                            }
                            else
                            {
                                multiEdgeInfo.Dispose = (acc) => multiArc.Dispose.Inline(acc);
                            }
                            if (multiArc.SkipToEnd == null)
                            {
                                multiEdgeInfo.SkipToEnd = null;
                            }
                            else
                            {
                                multiEdgeInfo.SkipToEnd = (ts, ev, acc) => multiArc.SkipToEnd.Inline(ts, ev, acc);
                            }

                            edgeList.Add(multiEdgeInfo);
                            continue;
                        }
                        if (edge is SingleElementArc<TPayload, TRegister> singleArc)
                        {
                            var edgeInfo = CreateSingleEdgeInfo(stream, targetNodeNumber, singleArc, "payloadoffset");
                            edgeList.Add(edgeInfo);
                            continue;
                        }
                        if (edge is ListElementArc<TPayload, TRegister> listArc)
                        {
                            var edgeInfo = CreateListEdgeInfo(stream, payloadRepresentation, targetNodeNumber, listArc, "payloadoffset");
                            edgeList.Add(edgeInfo);
                            continue;
                        }

                    }
                    template.edgeInfos.Add(Tuple.Create(sourceNodeNumber, edgeList));
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
                        if (arc is MultiElementArc<TPayload, TRegister, TAccumulator> multiArc)
                        {
                            var eps = EpsilonClosure(stream.afa, targetNode);

                            var multiEdgeInfo = new MultiEdgeInfo()
                            {
                                SourceNode = startState,
                                TargetNode = targetNode,
                                fromStartState = true,
                                EpsilonReachableNodes = EpsilonClosure(stream.afa, targetNode),
                                Initialize = (ts, reg) => multiArc.Initialize.Inline(ts, reg),
                                Accumulate = (ts, ev, reg, acc) => multiArc.Accumulate.Inline(ts, ev, reg, acc),
                                Fence = (ts, acc, reg) => multiArc.Fence.Inline(ts, acc, reg),
                            };
                            if (multiArc.Transfer == null)
                            {
                                multiEdgeInfo.Transfer = null;
                            }
                            else
                            {
                                multiEdgeInfo.Transfer = (ts, acc, reg) => multiArc.Transfer.Inline(ts, acc, reg);
                            }
                            if (multiArc.Dispose == null)
                            {
                                multiEdgeInfo.Dispose = (acc) => "// no dispose function";
                            }
                            else
                            {
                                multiEdgeInfo.Dispose = (acc) => multiArc.Dispose.Inline(acc);
                            }
                            if (multiArc.SkipToEnd == null)
                            {
                                multiEdgeInfo.SkipToEnd = null;
                            }
                            else
                            {
                                multiEdgeInfo.SkipToEnd = (ts, ev, acc) => multiArc.SkipToEnd.Inline(ts, ev, acc);
                            }
                            edgeList2.Add(multiEdgeInfo);
                            continue;
                        }
                        if (arc is SingleElementArc<TPayload, TRegister> singleArc)
                        {
                            var edgeInfo = CreateSingleEdgeInfo(stream, targetNode, singleArc, "payloadoffset");
                            edgeList2.Add(edgeInfo);
                            continue;
                        }
                        if (arc is ListElementArc<TPayload, TRegister> listArc)
                        {
                            var edgeInfo = CreateListEdgeInfo(stream, payloadRepresentation, targetNode, listArc, "payloadoffset");
                            edgeList2.Add(edgeInfo);
                            continue;
                        }
                    }

                    template.startEdgeInfos.Add(Tuple.Create(startState, edgeList2));
                }

                template.isSyncTimeSimultaneityFree = true; // The handwritten version doesn't make a distinction.
                template.keyEqualityComparer =
                    (left, right) =>
                        stream.Properties.KeyEqualityComparer.GetEqualsExpr().Inline(left, right);

                var expandedCode = template.TransformText();

                var assemblyReferences = Transformer.AssemblyReferencesNeededFor(typeof(TKey), typeof(TPayload), typeof(TRegister), typeof(Queue<>));
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
