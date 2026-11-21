struct VertexInput {
    float3 position : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    int4 bones : BLENDINDICES;
    float4 weights : BLENDWEIGHT;
    uint2 morphRange : TEXCOORD1;
};
struct VertexOutput { float4 position : SV_Position; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
struct Transform { row_major float4x4 skinMatrix; };
struct Offset { float3 shift; uint morph; };
StructuredBuffer<Transform> transforms : register(t0);
StructuredBuffer<Offset> offsets : register(t1);
StructuredBuffer<float> weights : register(t2);
cbuffer Scene : register(b0) {
    row_major float4x4 viewProjection;
    float4 keyDirection, keyColor, fillDirection, fillColor, rimDirection, rimColor;
};
cbuffer Material : register(b1) { float4 tint; float4 ambient; };
Texture2D albedo : register(t0);
SamplerState surfaceSampler : register(s0);
VertexOutput VSMain(VertexInput v) {
    float3 p=v.position;
    for(uint i=0;i<v.morphRange.y;++i) { Offset d=offsets[v.morphRange.x+i]; p+=d.shift*weights[d.morph]; }
    float4 world=0;float3 normal=0;
    [unroll] for(int k=0;k<4;++k){ if(v.weights[k]>0){
        world+=v.weights[k]*mul(float4(p,1),transforms[v.bones[k]].skinMatrix);
        normal+=v.weights[k]*mul(v.normal,(float3x3)transforms[v.bones[k]].skinMatrix);
    }}
    VertexOutput o;o.position=mul(float4(world.xyz,1),viewProjection);o.normal=normalize(normal);o.uv=v.uv;return o;
}
float4 PSMain(VertexOutput i) : SV_Target {
    float4 color=albedo.Sample(surfaceSampler,i.uv)*tint;
    clip(color.a-0.008);
    if(ambient.w<.5)clip(color.a-.995);else clip(.99499-color.a);
    float3 n=normalize(i.normal);
    float key=saturate(dot(n,keyDirection.xyz)*.68+.32);
    float fill=saturate(dot(n,fillDirection.xyz)*.66+.34);
    float rim=pow(1-abs(n.z),2.8)*saturate(dot(n,rimDirection.xyz)*.68+.32);
    float3 lit=ambient.xyz*.22+.25+key*keyColor.rgb+fill*fillColor.rgb;
    return float4(saturate(color.rgb*lit+rim*rimColor.rgb),saturate(color.a));
}
