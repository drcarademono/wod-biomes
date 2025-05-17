using UnityEngine;
using UnityEngine.Rendering;                          // for ShadowCastingMode
using System;
using System.IO;                                     // for File.Exists
using System.Reflection;
using System.Collections.Generic;
using DaggerfallWorkshop;                            // for DaggerfallUnity
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility.ModSupport;    // for TextureReplacement
using DaggerfallWorkshop.Utility;                    // for TextureReader
using DaggerfallWorkshop.Utility.AssetInjection;     // for TextureImport
using DaggerfallConnect;
using DaggerfallConnect.Arena2;                      // for TextureFile, RecordIndex
using DaggerfallConnect.Utility;                     // for FileUsage

namespace WorldOfDaggerfall
{
    public static class NatureBatchOverriderInstaller
    {
        [Invoke(StateManager.StateTypes.Start, 0)]
        public static void Init(InitParams _)
        {
            Debug.Log("[NatureBatch] Installer.Init");
            var go = new GameObject("NatureBatchOverrider");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<NatureBatchOverrider>();
        }
    }

    public class NatureBatchOverrider : MonoBehaviour
    {
        const int NEW_ARCHIVE = 10030;

        void OnEnable()
        {
            StreamingWorld.OnUpdateTerrainsEnd += ApplyOverrides;
        }

        void OnDisable()
        {
            StreamingWorld.OnUpdateTerrainsEnd -= ApplyOverrides;
        }

        void ApplyOverrides()
        {
            foreach (var batch in FindObjectsOfType<DaggerfallBillboardBatch>())
            {
                if (batch.TextureArchive != 501)
                    continue;

                bool inRegion = false;
                if (batch.GetComponentInParent<DaggerfallLocation>() is var loc && loc != null)
                {
                    var rn = loc.Summary.RegionName;
                    inRegion = (rn == "Abibon-Gora" || rn == "Kairou" || rn == "Pothago");
                }
                else if (batch.GetComponentInParent<DaggerfallTerrain>() is var terrain && terrain != null)
                {
                    int x = terrain.MapPixelX, y = terrain.MapPixelY;
                    var maps = DaggerfallUnity.Instance.ContentReader.MapFileReader;
                    int region = maps.GetPoliticIndex(x, y) - 128;
                    inRegion = (region == 43 || region == 44 || region == 45);
                }

                if (!inRegion)
                    continue;

                CustomBillboardHelper.RevisedSetMaterial(batch, NEW_ARCHIVE, true);
                batch.Apply();
            }
        }
    }

    static class CustomBillboardHelper
    {
        // stash our generated albedo atlas so RevisedSetMaterial can grab it
        static Texture2D _lastAtlas = null;

        /// <summary>
        /// Replacement for DaggerfallBillboardBatch.SetMaterial that supports archives > 511.
        /// </summary>
        public static void RevisedSetMaterial(DaggerfallBillboardBatch batch, int archive, bool force)
        {
            var bt   = typeof(DaggerfallBillboardBatch);
            var curF = bt.GetField("currentArchive", BindingFlags.Instance|BindingFlags.NonPublic);
            int cur  = (int)curF.GetValue(batch);
            if (archive == cur && !force) return;

            // 1) pull down all atlas data
            RevisedGetTextureResults(
                archive,
                out Rect[]       atlasRects,
                out RecordIndex[] atlasIndices,
                out int[]        frameCounts,
                out Vector2[]    recordSizes,
                out Vector2[]    recordScales,
                out int          key);

            // 2) build your material using the freshly‐packed albedo atlas
            string shaderName = DaggerfallUnity.Settings.NatureBillboardShadows
                ? MaterialReader._DaggerfallBillboardBatchShaderName
                : MaterialReader._DaggerfallBillboardBatchNoShadowsShaderName;
            var mat = new Material(Shader.Find(shaderName))
            {
                mainTexture = _lastAtlas
            };

            // 3) assemble a CachedMaterial
            var cm = new CachedMaterial
            {
                atlasRects       = atlasRects,
                atlasIndices     = atlasIndices,
                atlasFrameCounts = frameCounts,
                recordSizes      = recordSizes,
                recordScales     = recordScales,
                key              = key
            };

            // 4) shove everything back into DaggerfallBillboardBatch
            bt.GetField("cachedMaterial", BindingFlags.Instance|BindingFlags.NonPublic)
              .SetValue(batch, cm);
            bt.GetField("TextureArchive", BindingFlags.Instance|BindingFlags.Public)
              .SetValue(batch, archive);
            curF.SetValue(batch, archive);

            // 5) assign material to renderer
            var rend = batch.GetComponent<MeshRenderer>();
            rend.sharedMaterial    = mat;
            rend.receiveShadows    = false;
            rend.shadowCastingMode = (archive == TextureReader.LightsTextureArchive)
                                     ? ShadowCastingMode.Off
                                     : ShadowCastingMode.TwoSided;
        }

        /// <summary>
        /// Packs both vanilla and mod textures into a single atlas, and returns
        /// all the arrays you need plus an integer key.
        /// </summary>
        public static void RevisedGetTextureResults(
            int archive,
            out Rect[]       atlasRects,
            out RecordIndex[] atlasIndices,
            out int[]        frameCounts,
            out Vector2[]    recordSizes,
            out Vector2[]    recordScales,
            out int          key)
        {
            // prepare settings
            var settings = new GetTextureSettings
            {
                archive      = archive,
                stayReadable = true,
                atlasMaxSize = DaggerfallUnity.Settings.AssetInjection ? 4096 : 2048,
                atlasPadding = 4
            };

            // results container
            var results = new GetTextureResults
            {
                atlasSizes       = new List<Vector2>(),
                atlasScales      = new List<Vector2>(),
                atlasOffsets     = new List<Vector2>(),
                atlasFrameCounts = new List<int>()
            };

            // load Arena2 .tex if present
            if (settings.textureFile == null)
            {
                string path = Path.Combine(DaggerfallUnity.Instance.Arena2Path,
                                           TextureFile.IndexToFileName(settings.archive));
                if (File.Exists(path))
                    settings.textureFile = new TextureFile(path, FileUsage.UseMemory, true);
            }
            var texFile = settings.textureFile;

            bool hasNormals = false, hasEmissive = false, hasAnim = false;
            var importMode = settings.atlasMaxSize == 4096
                         ? TextureImport.AllLocations
                         : TextureImport.None;

            var albedos     = new List<Texture2D>();
            var normalsList = new List<Texture2D>();
            var emissions   = new List<Texture2D>();
            var indicesList = new List<RecordIndex>();

            int recordCount = texFile?.RecordCount ?? 0;
            var reader = new TextureReader(DaggerfallUnity.Instance.Arena2Path);

            if (recordCount > 0)
            {
                // vanilla Arena2 archive
                for (int rec = 0; rec < recordCount; rec++)
                {
                    settings.record = rec;
                    int frames = texFile.GetFrameCount(rec);
                    if (frames > 1) hasAnim = true;

                    var size   = texFile.GetSize(rec);
                    var scale  = texFile.GetScale(rec);
                    var offset = texFile.GetOffset(rec);

                    indicesList.Add(new RecordIndex
                    {
                        startIndex = albedos.Count,
                        frameCount = frames,
                        width      = size.Width,
                        height     = size.Height
                    });

                    for (int f = 0; f < frames; f++)
                    {
                        settings.frame = f;
                        var r = reader.GetTexture2D(settings, SupportedAlphaTextureFormats.ARGB32, importMode);
                        albedos.Add(r.albedoMap);
                        if (r.normalMap  != null) { normalsList.Add(r.normalMap);   hasNormals = true; }
                        if (r.emissionMap!= null) { emissions.Add(r.emissionMap); hasEmissive= true; }
                    }

                    results.atlasSizes.Add (new Vector2(size.Width, size.Height));
                    results.atlasScales.Add(new Vector2(scale.Width, scale.Height));
                    results.atlasOffsets.Add(new Vector2(offset.X, offset.Y));
                    results.atlasFrameCounts.Add(frames);
                }
            }
            else
            {
                // no vanilla archive → load mods
                ProcessCustomTextures(settings, albedos, normalsList, emissions, indicesList, results);
            }

            // pack albedo into _lastAtlas
            _lastAtlas   = new Texture2D(settings.atlasMaxSize, settings.atlasMaxSize, TextureFormat.ARGB32, true);
            atlasRects   = _lastAtlas.PackTextures(albedos.ToArray(), settings.atlasPadding, settings.atlasMaxSize, false);
            if (albedos.Count > 0)
            {
                var sample = albedos[0];
                _lastAtlas.filterMode = sample.filterMode;
                _lastAtlas.wrapMode   = sample.wrapMode;
                _lastAtlas.anisoLevel = sample.anisoLevel;
            }
            atlasIndices = indicesList.ToArray();
            frameCounts  = results.atlasFrameCounts.ToArray();

            // (you can also pack normals/emissions here if desired)

            recordSizes  = results.atlasSizes.ToArray();
            recordScales = results.atlasScales.ToArray();

            if (!WODBiomes.VEModEnabled)for (int i = 0; i < recordSizes.Length; i++) // Scale textures x2
                recordSizes[i] *= 2f;

            key          = archive; // or hash all arrays for a unique key
        }

        /// <summary>
        /// Your existing mod‐texture fallback logic, verbatim.
        /// </summary>
        static void ProcessCustomTextures(
            GetTextureSettings  settings,
            List<Texture2D>     albedoTextures,
            List<Texture2D>     normalTextures,
            List<Texture2D>     emissionTextures,
            List<RecordIndex>   indices,
            GetTextureResults   results)
        {
            for (int record = 0; record < 256; record++)
            {
                settings.record = record;
                int frameCount = 0;
                var modFrames = new List<Texture2D>();

                while (true)
                {
                    settings.frame = frameCount;
                    if (TextureReplacement.TryImportTexture(
                        settings.archive, record, frameCount, out Texture2D modAlbedo))
                    {
                        modFrames.Add(modAlbedo);
                        albedoTextures.Add(modAlbedo);
                        frameCount++;
                    }
                    else break;
                }

                if (frameCount > 0)
                {
                    indices.Add(new RecordIndex
                    {
                        startIndex = albedoTextures.Count - frameCount,
                        frameCount = frameCount,
                        width      = modFrames[0].width,
                        height     = modFrames[0].height
                    });

                    results.atlasSizes.Add       (new Vector2(modFrames[0].width,  modFrames[0].height));
                    results.atlasScales.Add      (Vector2.one);
                    results.atlasOffsets.Add     (Vector2.zero);
                    results.atlasFrameCounts.Add (frameCount);
                }
            }
        }
    }
}

