﻿using Core.Util;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Core.Rendering.Octree
{
    [BurstCompile(FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance, FloatPrecision = FloatPrecision.Low)]
    public unsafe struct SparseOctree : IJobParallelFor
    {
        [NativeDisableUnsafePtrRestriction, NativeDisableContainerSafetyRestriction] public Node root;
        [NativeDisableContainerSafetyRestriction] public NativeArray<NativeList<float3>> featurePoints;

        public readonly static SharedStatic<Pool> pool = SharedStatic<Pool>.GetOrCreate<Pool, Node>();
        public readonly static SharedStatic<Settings> settings = SharedStatic<Settings>.GetOrCreate<Settings, SparseOctree>();
        public SparseOctree(in Settings settings)
        {
            SparseOctree.settings.Data = settings;
            pool.Data = new Pool(settings.poolDepth);
            featurePoints = new(JobsUtility.ThreadIndexCount, Allocator.Persistent);

            for (int i = 0; i < JobsUtility.ThreadIndexCount; i++)
            {
                featurePoints[i] = new(8192, Allocator.Persistent);
            }

            root = *pool.Data.Lease(1);
            root.AssignSubnodes(pool.Data.Lease(8), 255);
        }
        public readonly void Dispose()
        {
            pool.Data.Dispose();
            Debug.Log("Disposed Octree");
        }

        public void Reset()
        {
            pool.Data.Dispose();
            pool.Data = new(settings.Data.poolDepth);

            root = *pool.Data.Lease(1);
            root.AssignSubnodes(pool.Data.Lease(8), 255); 
        }
        public void Execute(int index)
        {
            var vertices = featurePoints[JobsUtility.ThreadIndex];
            vertices.Clear();

            var edgeIntersection = new NativeList<float4>(12, Allocator.Temp);
            var edgeNormal = new NativeList<float4>(12, Allocator.Temp);

            Subdivide(ref root[index], 1, SubnodePosition(index, 1, 0), ref vertices, edgeIntersection.GetUnsafePtr(), edgeNormal.GetUnsafePtr());
            
            featurePoints[JobsUtility.ThreadIndex] = vertices;

            edgeIntersection.Dispose();
            edgeNormal.Dispose();
        }
        private readonly void Subdivide(ref Node node, in int depth, in float3 position, ref NativeList<float3> vertices, in float4* edgeIntersection, in float4* edgeNormals)
        {
            // Check if on the chunk depth
            //   Assign a chunk to the node and exit
            var _depth = depth + 1;
            if (_depth > Util.Settings.MaxDepth)
            {
                //pool.Data.Lease(out int index, out Voxel* data);
                //node.AssignChunk(index, data);
                return;
            }

            var _count = 0;
            var _map = 0;
            var tempData = new NativeList<HermiteData>(8, Allocator.Temp);
            var tempPosition = new NativeList<float3>(8, Allocator.Temp);
            for (int i = 0; i < 8; i++)
            {
                float3 _position = SubnodePosition(i, _depth, position);
                if (Evaluate(_position, _depth, out HermiteData _data) && _depth <= Util.Settings.MaxDepth - SubnodeLOD(_position))
                {
                    _map |= 1 << i;
                    tempData.AddNoResize(_data);
                    tempPosition.AddNoResize(_position);
                    _count++;
                }
            }

            // Generate vertices
            var totalEdges = Table.EdgeIndexTable[_map].Length / 2;
            if (false && totalEdges > 0)
            {
                // Generate edge points
                int edgeCount = 0;
                for (int edge = 0; edge < totalEdges; edge++)
                {
                    int ei1 = Table.EdgeIndexTable[_map][edge * 2 + 0];
                    int ei2 = Table.EdgeIndexTable[_map][edge * 2 + 1];

                    float d1 = tempData[ei1].Distance;
                    float d2 = tempData[ei2].Distance;

                    if (math.sign(d1) != math.sign(d2) || d1 == 0)
                    {
                        float3 p1 = tempPosition[ei1];
                        float3 p2 = tempPosition[ei2];

                        float3 n1 = tempData[ei1].Gradient;
                        float3 n2 = tempData[ei2].Gradient;

                        float t = math.saturate(-d1 / (d2 - d1));

                        float3 p = math.lerp(p1, p2, t);
                        float3 n = math.lerp(n1, n2, t);

                        edgeIntersection[edgeCount] = new(p, 0);
                        edgeNormals[edgeCount] = new(n, 0);
                        edgeCount++;
                    }
                    else if (settings.Data.addSameSignedEdges)
                    {
                        int closer = math.abs(d1) < math.abs(d2) ? ei1 : ei2;
                        float3 n = tempData[closer].Gradient;
                        float3 p = tempPosition[closer] + n * -(closer == ei1 ? d1 : d2);
                        edgeIntersection[edgeCount] = new(p, 0);
                        edgeNormals[edgeCount] = new(n, 0);
                        edgeCount++;
                    }
                }

                if (edgeCount >= settings.Data.pointsRequiredForQEF)
                {
                    var p = QEF.Solve(edgeIntersection, edgeNormals, edgeCount, out _);
                    vertices.AddNoResize(p.xyz);
                }
                else if (settings.Data.addSingleEdges && edgeCount == 1)
                {
                    vertices.AddNoResize(edgeIntersection[0].xyz);
                }
            }

            // Only continue if there are subnodes and they are all not the same
            if (_count != 0)
            {
                // Assign data and subdivide the subnodes
                node.AssignSubnodes(pool.Data.Lease(_count), (byte)_map);
                for (int i = 0; i < _count; i++)
                {
                    node[i].data = new(tempData[i]);
                    Subdivide(ref node[i], _depth, tempPosition[i], ref vertices, edgeIntersection, edgeNormals);
                }
            }
        }

        private readonly bool Evaluate(in float3 position, in int depth, out HermiteData data)
        {
            float sD = SDFs.SDSphere(position, settings.Data.sphereRadius, out var sG);
            data = new(sD, 0, sG);

            float3 surfacePoint = data.Point + position;
            int r = (int)math.pow(math.pow(8, depth), 1f / 3f) / 2;
            int pI = IndexPosition.CellIndex(surfacePoint, r);
            int oI = IndexPosition.CellIndex(position, r);
            return pI == oI;
            //return SDFs.SDBox(data.Point, SubnodeLength(depth) * settings.Data.subdivisionFactor) <= 0;
            //return math.abs(data.Distance) <= Table.HalfDiagonal[depth] * settings.Data.subdivisionFactor;
        }
        public readonly float SubnodeLOD(in float3 position)
        {
            // math.log2(SubnodeLength(depth) / SubnodeLength(Settings.MaxDepth))
            float distLod = math.distance(settings.Data.octreeCenter, position);
            distLod /= Util.Settings.HalfWorldSize; // normalize to world size
            distLod *= settings.Data.lodFactor; // upper bound is the lod factor
            return distLod;
        }
        public static float3 SubnodePosition(in int index, in int depth, in float3 parentempPosition)
        {
            float3 childPosition = Table.UnitCorners[index];
            childPosition *= Table.SubnodeLength[depth];
            return childPosition + parentempPosition;
        }
        public static void FindNext1(in int map, ref int start, out int next1BitIndex)
        {
#if DEBUG
            if (map == 0 || start < 0 || start > 7) throw new("Invalid bitmap or index input");
#endif
            for (int i = start; i < 8; i++)
            {
                int bit = 1 << i;
                if ((bit & map) == bit)
                {
                    next1BitIndex = i;
                    return;
                }
            }
            throw new("Unable to find an on bit");
        }
        public static float SubnodeLength(in int octantDepth)
        {
            return Table.SubnodeLength[octantDepth];
        }
        public readonly int NodesUsed()
        {
            int count = 0;
            for (int i = 0; i < pool.Data.lengths.Length; i++)
            {
                count += pool.Data.lengths[i];
            }
            return pool.Data.lengths.Length * pool.Data.nodesPerThread - count;
        }
    }
}