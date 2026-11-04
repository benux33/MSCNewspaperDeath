using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeathNewspaper
{
    internal sealed class BodyPhotoScope : IDisposable
    {
        struct Saved { public Renderer Renderer; public ShadowCastingMode Shadows; public bool ForceOff; public bool Offscreen; }
        readonly List<Saved> saved = new List<Saved>();
        public Bounds Bounds { get; private set; }
        public BodyPhotoScope(Component body)
        {
            bool found = false;
            var bounds = new Bounds(body.transform.position, Vector3.one);
            try
            {
                foreach (var renderer in body.GetComponentsInChildren<Renderer>())
                {
                    if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                    var skin = renderer as SkinnedMeshRenderer;
                    saved.Add(new Saved { Renderer = renderer, Shadows = renderer.shadowCastingMode,
                        ForceOff = renderer.forceRenderingOff, Offscreen = skin != null && skin.updateWhenOffscreen });
                    if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) renderer.shadowCastingMode = ShadowCastingMode.On;
                    renderer.forceRenderingOff = false;
                    if (skin != null) skin.updateWhenOffscreen = true;
                    if (!renderer.enabled) continue; // Preserve selected LOD and equipment visibility.
                    if (skin != null && skin.sharedMesh != null)
                    {
                        var baked = new Mesh();
                        try
                        {
                            skin.BakeMesh(baked);
                            var local = baked.bounds;
                            for (int corner = 0; corner < 8; corner++)
                            {
                                var point = local.center + Vector3.Scale(local.extents, new Vector3(
                                    (corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                                point = skin.transform.TransformPoint(point);
                                if (!found) { bounds = new Bounds(point, Vector3.zero); found = true; }
                                else bounds.Encapsulate(point);
                            }
                        }
                        finally { UnityEngine.Object.Destroy(baked); }
                    }
                    else if (renderer.bounds.size.magnitude < 5)
                    { if (!found) { bounds = renderer.bounds; found = true; } else bounds.Encapsulate(renderer.bounds); }
                }
                bounds.Expand(0.35f);
                Bounds = bounds;
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            foreach (var state in saved)
            {
                if (state.Renderer == null) continue;
                state.Renderer.shadowCastingMode = state.Shadows;
                state.Renderer.forceRenderingOff = state.ForceOff;
                var skin = state.Renderer as SkinnedMeshRenderer;
                if (skin != null) skin.updateWhenOffscreen = state.Offscreen;
            }
            saved.Clear();
        }
    }
}

