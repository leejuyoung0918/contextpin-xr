Shader "ContextPin/Assembly Overlay"
{
    Properties { _Color ("Tint", Color) = (0.16,0.8,0.86,0.5) }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            struct Vertex
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varying
            {
                float4 position : SV_POSITION;
                float3 normal : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            fixed4 _Color;
            Varying vert(Vertex input)
            {
                Varying output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_OUTPUT(Varying, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.position = UnityObjectToClipPos(input.vertex);
                output.normal = UnityObjectToWorldNormal(input.normal);
                return output;
            }
            fixed4 frag(Varying input) : SV_Target
            {
                float shade = 0.63 + 0.37 * saturate(dot(normalize(input.normal), normalize(float3(-0.5, 0.8, -0.4))));
                return fixed4(_Color.rgb * shade, _Color.a);
            }
            ENDCG
        }
    }
}
