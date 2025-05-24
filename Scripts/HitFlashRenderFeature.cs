using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
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
        public HitFlashRenderPass(HitFlashSettings settings)
        {
            this.settings = settings;
            hitflashBlitMaterial = new Material(Shader.Find("Hitflash/Blit"));
            hitflashBlitMaterial.SetColor("color", settings.color);
        }

        public void Dispose()
        {
            DestroyImmediate(hitflashBlitMaterial);
            FlashMaskRT?.Release();
        }

        private RTHandle FlashMaskRT;
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var camTargetDesc = renderingData.cameraData.cameraTargetDescriptor;
            if (FlashMaskRT == null || FlashMaskRT.rt.width != camTargetDesc.width || FlashMaskRT.rt.height != camTargetDesc.height)
            {
                if (FlashMaskRT != null) FlashMaskRT.Release();
                FlashMaskRT = RTHandles.Alloc(width: camTargetDesc.width, height: camTargetDesc.height, name: "hitFlashMask", colorFormat: UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm);
            }

            //remove old entries
            var minValidStartTime = Time.time - settings.duration;
            while (activeHitFlashes.Count > 0 && activeHitFlashes[0].startTime < minValidStartTime)
                activeHitFlashes.RemoveAt(0);

            //render active entries
            var cmd = CommandBufferPool.Get();
            cmd.name = "DrawHitFlash";
            for (int i = 0; i < activeHitFlashes.Count; i++)
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                var hitflash = activeHitFlashes[i];
                if (hitflashBlitMaterial == null) break;
                hitflashBlitMaterial.SetFloat("t", (hitflash.startTime - Time.time) / settings.duration);
                for (int r = 0; r < hitflash.renderTargets.Length; r++)
                {
                    var renderTarget = hitflash.renderTargets[r];
                    if (renderTarget.Renderer == null) continue;
                    if (renderTarget.Mesh == null) continue;
                    var mesh = renderTarget.Mesh;
                    var materials = renderTarget.Materials;
                    for (int m = 0; m < mesh.subMeshCount; m++)
                    {
                        var shaderPass = materials[m].FindPass("ForwardLit");
                        cmd.SetRenderTarget(FlashMaskRT, depth: renderingData.cameraData.renderer.cameraDepthTargetHandle);
                        cmd.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 1f, 0);
                        cmd.DrawRenderer(renderTarget.Renderer, materials[m], m, shaderPass);
                    }
                    cmd.Blit(FlashMaskRT, renderingData.cameraData.renderer.cameraColorTargetHandle, mat: hitflashBlitMaterial);
                    context.ExecuteCommandBuffer(cmd);
                    cmd.Clear();
                }
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }
    }
}
