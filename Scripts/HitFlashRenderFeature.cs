using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
using UnityEngine.U2D;


public class HitFlashRenderFeature : ScriptableRendererFeature
{
    private class HitflashInfo
    {
        public GameObject target;
        public IRenderTarget[] renderTargets;
        public float startTime;
        public HitflashInfo(GameObject target)
        {
            this.target = target;
            var renderers = target.GetComponentsInChildren<Renderer>();
            var renderTargetList = new List<IRenderTarget>();
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] is MeshRenderer) renderTargetList.Add(new MeshRendererRenderTarget((MeshRenderer)renderers[i]));
                if (renderers[i] is SkinnedMeshRenderer) renderTargetList.Add(new SkinnedMeshRendererRenderTarget((SkinnedMeshRenderer)renderers[i]));
                if (renderers[i] is SpriteRenderer) renderTargetList.Add(new SpriteRendererRenderTarget((SpriteRenderer)renderers[i]));
            }
            this.renderTargets = renderTargetList.ToArray();
            this.startTime = Time.time;
        }

        private class SkinnedMeshRendererRenderTarget : IRenderTarget
        {
            public SkinnedMeshRendererRenderTarget(SkinnedMeshRenderer renderer) => smr = renderer;
            public Renderer Renderer => smr;
            private SkinnedMeshRenderer smr;
            public Mesh Mesh => smr != null ? smr.sharedMesh : null;
            public Material[] Materials => smr != null ? smr.sharedMaterials : null;
        }
        public class SpriteRendererRenderTarget : IRenderTarget
        {
            public SpriteRendererRenderTarget(SpriteRenderer renderer)
            {
                sr = renderer;
                var spriteMesh = new Mesh();
                Vector3[] verts = new Vector3[sr.sprite.vertices.Length];
                for (int i = 0; i < sr.sprite.vertices.Length; i++)
                    verts[i] = sr.sprite.vertices[i];
                NativeArray<ushort> indices = sr.sprite.GetIndices();
                int[] meshIndices = new int[indices.Length];
                for (int i = 0; i < meshIndices.Length; i++)
                    meshIndices[i] = indices[i];
                spriteMesh.SetVertices(verts);
                spriteMesh.SetIndices(meshIndices, MeshTopology.Triangles, 0);
                Mesh = spriteMesh;
            }
            public Renderer Renderer => sr;
            private SpriteRenderer sr;
            public Mesh Mesh { get; private set; }
            public Material[] Materials => sr != null ? sr.sharedMaterials : null;
        }

        private class MeshRendererRenderTarget : IRenderTarget
        {
            public MeshRendererRenderTarget(MeshRenderer renderer) => mf = (mr = renderer).GetComponent<MeshFilter>();
            public Renderer Renderer => mr;
            private MeshFilter mf;
            private MeshRenderer mr;
            public Mesh Mesh => mf != null ? mf.sharedMesh : null;
            public Material[] Materials => mr != null ? mr.sharedMaterials : null;
        }
        public interface IRenderTarget
        {
            public Renderer Renderer { get; }
            public Mesh Mesh { get; }
            public Material[] Materials { get; }
        }
    }

    private static readonly List<HitflashInfo> activeHitFlashes = new();
    public static void Flash(GameObject target)
    {
        activeHitFlashes.Add(new(target));
    }

    [System.Serializable]
    private class HitFlashSettings
    {
        public Color32 color;
        public Material hitflashBlitMaterial;
        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        public float duration;
    }

    [SerializeField] private HitFlashSettings settings;

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(pass);
    }

    private HitFlashRenderPass pass;
    public override void Create()
    {
        pass = new HitFlashRenderPass(settings);
        pass.renderPassEvent = settings.renderPassEvent;
    }

    private class HitFlashRenderPass : ScriptableRenderPass, IDisposable
    {
        private HitFlashSettings settings;
        private Material hitflashBlitMaterial;
        RenderTextureDescriptor hitFlashMaskTextureDescriptor;
        public HitFlashRenderPass(HitFlashSettings settings)
        {
            this.settings = settings;
            hitflashBlitMaterial = settings.hitflashBlitMaterial ?? new Material(Shader.Find("Hitflash/Blit"));
            hitflashBlitMaterial?.SetColor("color", settings.color);
            hitFlashMaskTextureDescriptor = new(Screen.width, Screen.height, RenderTextureFormat.Default, 0);
        }

        public void Dispose()
        {
            DestroyImmediate(hitflashBlitMaterial);
        }

        public class HitflashPassData
        {
            public HitflashInfo hitflashInfo;
            public TextureHandle hitFlashMaskTextureHandle;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            if (resourceData.isActiveTargetBackBuffer) return;

            hitFlashMaskTextureDescriptor.width = cameraData.cameraTargetDescriptor.width;
            hitFlashMaskTextureDescriptor.height = cameraData.cameraTargetDescriptor.height;
            hitFlashMaskTextureDescriptor.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm;
            hitFlashMaskTextureDescriptor.depthBufferBits = 0;

            var srcCamColorHandle = resourceData.activeColorTexture;
            var srcCamDepthHandle = resourceData.activeDepthTexture;
            var hitFlashMaskTextureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, hitFlashMaskTextureDescriptor, "Hitflash_Mask", false);

            // This check is to avoid an error from the material preview in the scene
            if (!(srcCamColorHandle.IsValid() && srcCamDepthHandle.IsValid() && hitFlashMaskTextureHandle.IsValid()))
                return;

            //remove old entries
            var minValidStartTime = Time.time - settings.duration;
            while (activeHitFlashes.Count > 0 && activeHitFlashes[0].startTime < minValidStartTime)
                activeHitFlashes.RemoveAt(0);
            if (hitflashBlitMaterial == null) return;
            var activeFlashes = activeHitFlashes;
            for (int i = 0; i < activeFlashes.Count; i++)
            {
                var hitflash = activeFlashes[i];
                using (var renderPass = renderGraph.AddRasterRenderPass<HitflashPassData>("Hitflash-Render", out var data))
                {
                    data.hitflashInfo = hitflash;
                    data.hitFlashMaskTextureHandle = hitFlashMaskTextureHandle;
                    renderPass.SetRenderAttachment(hitFlashMaskTextureHandle, 0);
                    renderPass.SetRenderAttachmentDepth(srcCamDepthHandle, 0);
                    renderPass.SetRenderFunc<HitflashPassData>(ExecuteHitflashRenderPass);
                }
                var blitPropertyBlock = new MaterialPropertyBlock();
                blitPropertyBlock.SetFloat("t", (hitflash.startTime - Time.time) / settings.duration);
                RenderGraphUtils.BlitMaterialParameters parameters = new(hitFlashMaskTextureHandle, srcCamColorHandle, hitflashBlitMaterial, 0)
                {
                    sourceTexturePropertyID = Shader.PropertyToID("_MainTex"),
                    propertyBlock = blitPropertyBlock,
                    geometry = RenderGraphUtils.FullScreenGeometryType.ProceduralTriangle
                };
                renderGraph.AddBlitPass(parameters);
            }
        }

        private void ExecuteHitflashRenderPass(HitflashPassData data, RasterGraphContext context)
        {
            //render active entries
            var cmd = context.cmd;
            cmd.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 1f, 0);
            for (int r = 0; r < data.hitflashInfo.renderTargets.Length; r++)
            {
                var renderTarget = data.hitflashInfo.renderTargets[r];
                if (renderTarget.Renderer == null) continue;
                if (renderTarget.Mesh == null) continue;
                var mesh = renderTarget.Mesh;
                var materials = renderTarget.Materials;
                for (int m = 0; m < mesh.subMeshCount; m++)
                {
                    var shaderPass = materials[m].FindPass("ForwardLit");
                    cmd.DrawRenderer(renderTarget.Renderer, materials[m], m, shaderPass);
                }
            }
        }
    }
}
