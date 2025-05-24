Shader "Hitflash/Blit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" }
        LOD 100
        ZTest Always
        ZWrite Off
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            // Generates a triangle in homogeneous clip space, s.t.
            // v0 = (-1, -1, 1), v1 = (3, -1, 1), v2 = (-1, 3, 1).
            float2 GetFullScreenTriangleTexCoord(uint vertexID)
            {
            #if UNITY_UV_STARTS_AT_TOP
                return float2((vertexID << 1) & 2, 1.0 - (vertexID & 2));
            #else
                return float2((vertexID << 1) & 2, vertexID & 2);
            #endif
            }

            float4 GetFullScreenTriangleVertexPosition(uint vertexID, float z = UNITY_NEAR_CLIP_VALUE)
            {
                // note: the triangle vertex position coordinates are x2 so the returned UV coordinates are in range -1, 1 on the screen.
                float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
                float4 pos = float4(uv * 2.0 - 1.0, z, 1.0);
            #ifdef UNITY_PRETRANSFORM_TO_DISPLAY_ORIENTATION
                pos = ApplyPretransformRotation(pos);
            #endif
                return pos;
            }

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert (uint v : SV_VertexID)
            {
                v2f output;
                output.vertex = GetFullScreenTriangleVertexPosition(v);
                output.uv     = GetFullScreenTriangleTexCoord(v);
                return output;
            }

            float4 color;
            float t;

            fixed4 frag (v2f i) : SV_Target0
            {
                fixed4 mask = tex2D(_MainTex, i.uv);
                return float4(color.rgb, color.a *  mask.a * (1 - t * t));
            }
            ENDCG
        }
    }
}
