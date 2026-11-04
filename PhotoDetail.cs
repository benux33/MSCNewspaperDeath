using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeathNewspaper
{
    internal sealed class PhotoDetail : IDisposable
    {
        readonly Dictionary<Texture2D, int> textures = new Dictionary<Texture2D, int>();
        readonly Dictionary<SkinnedMeshRenderer, bool> skins = new Dictionary<SkinnedMeshRenderer, bool>();
        public PhotoDetail(Vector3 center)
        {
            try
            {
                foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (renderer.bounds.SqrDistance(center) > 35f * 35f) continue;
                    var skin = renderer as SkinnedMeshRenderer;
                    if (skin != null) { skins[skin] = skin.updateWhenOffscreen; skin.updateWhenOffscreen = true; }
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material == null) continue;
                        foreach (int id in material.GetTexturePropertyNameIDs()) Request(material.GetTexture(id) as Texture2D);
                    }
                }
                // Deferred blood materials need not belong to a normal scene Renderer.
                foreach (var texture in Resources.FindObjectsOfTypeAll<Texture2D>())
                {
                    string name = texture.name.ToLowerInvariant();
                    if (name.Contains("blood") || name.Contains("decal")) Request(texture);
                }
            }
            catch { Dispose(); throw; }
        }
        void Request(Texture2D texture)
        {
            if (texture == null || !texture.streamingMipmaps || textures.ContainsKey(texture)) return;
            textures[texture] = texture.requestedMipmapLevel;
            texture.requestedMipmapLevel = 0;
        }
        public int Pending
        {
            get
            {
                int count = 0;
                foreach (var pair in textures)
                    if (pair.Key != null && !pair.Key.IsRequestedMipmapLevelLoaded()) count++;
                return count;
            }
        }
        public void Dispose()
        {
            foreach (var pair in textures)
                if (pair.Key != null)
                { if (pair.Value < 0) pair.Key.ClearRequestedMipmapLevel(); else pair.Key.requestedMipmapLevel = pair.Value; }
            foreach (var pair in skins) if (pair.Key != null) pair.Key.updateWhenOffscreen = pair.Value;
            textures.Clear(); skins.Clear();
        }
    }
}
