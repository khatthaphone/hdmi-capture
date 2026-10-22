using System.Numerics;
using System.Runtime.InteropServices;

namespace HDMICaptain.Media.Render;

/// <summary>
/// HLSL compiled at runtime, like MetalRenderer's shader source, with the same maths.
/// Pass 1 (PSConvert) turns the source planes into RGBA at source size: YCbCr → RGB, adjustments, LUT.
/// Pass 2 (PSScale) draws that texture into the swap chain viewport with the scale mode's source rect.
/// </summary>
internal static class Shaders
{
    // Input layouts; keep in sync with InputKind.
    public const string Source = """
        cbuffer Params : register(b0)
        {
            float4 row0;      // YCbCr -> RGB matrix rows (xyz)
            float4 row1;
            float4 row2;
            float4 offset;    // subtracted before scaling (xyz)
            float4 scale;     // range expansion (xyz)
            float4 adjust;    // brightness, contrast, saturation, 1/gamma
            float4 sharpen;   // amount, unused
            float4 lut;       // intensity, size
            float4 lutMin;    // domain min (xyz)
            float4 lutMax;    // domain max (xyz)
            float4 uvRect;    // visible source rect: x0, y0, x1, y1
            int4 input;       // x: 0 NV12, 1 YUY2, 2 planar, 3 BGRA
        };

        Texture2D<float4> plane0 : register(t0);
        Texture2D<float4> plane1 : register(t1);
        Texture2D<float4> plane2 : register(t2);
        Texture3D<float4> table : register(t3);
        SamplerState smp : register(s0);      // linear or nearest (pass 2); linear for chroma (pass 1)
        SamplerState lutSmp : register(s1);   // linear, clamp

        struct VSOut
        {
            float4 pos : SV_Position;
            float2 uv : TEXCOORD0;
        };

        VSOut VSQuad(uint id : SV_VertexID)
        {
            float2 c = float2(id & 1, id >> 1);
            VSOut o;
            o.pos = float4(c.x * 2 - 1, 1 - c.y * 2, 0, 1);
            o.uv = lerp(uvRect.xy, uvRect.zw, c);
            return o;
        }

        float3 applyLUT(float3 rgb)
        {
            if (lut.x <= 0) { return rgb; }
            float n = lut.y;
            float3 c = saturate((rgb - lutMin.xyz) / (lutMax.xyz - lutMin.xyz));
            float3 graded = table.SampleLevel(lutSmp, c * ((n - 1) / n) + 0.5 / n, 0).rgb;
            return lerp(rgb, saturate(graded), lut.x);
        }

        // Identity when adjust == (0, 1, 1, 1).
        float3 adjustColor(float3 rgb)
        {
            rgb = (rgb - 0.5) * adjust.y + 0.5 + adjust.x;
            float luma = dot(rgb, float3(0.2126, 0.7152, 0.0722));
            rgb = lerp(float3(luma, luma, luma), rgb, adjust.z);
            rgb = saturate(rgb);
            return adjust.w == 1.0 ? rgb : pow(rgb, adjust.www);
        }

        float lumaAt(int2 p)
        {
            return plane0.Load(int3(p, 0)).r;
        }

        float4 PSConvert(VSOut i) : SV_Target
        {
            int2 p = int2(i.pos.xy);
            float3 rgb;
            if (input.x == 3)
            {
                rgb = plane0.Load(int3(p, 0)).rgb;
            }
            else
            {
                float y;
                float2 c;
                if (input.x == 1)
                {
                    // YUY2 as RGBA8 at half width: r = Y0, g = Cb, b = Y1, a = Cr.
                    float4 t = plane0.Load(int3(p.x >> 1, p.y, 0));
                    y = (p.x & 1) ? t.b : t.r;
                    c = t.ga;
                }
                else
                {
                    y = lumaAt(p);
                    if (sharpen.x > 0)
                    {
                        float neighbours = lumaAt(p + int2(1, 0)) + lumaAt(p - int2(1, 0)) + lumaAt(p + int2(0, 1)) + lumaAt(p - int2(0, 1));
                        y += (y - neighbours * 0.25) * sharpen.x * 2.0;
                    }
                    c = input.x == 0
                        ? plane1.SampleLevel(smp, i.uv, 0).rg
                        : float2(plane1.SampleLevel(smp, i.uv, 0).r, plane2.SampleLevel(smp, i.uv, 0).r);
                }
                float3 v = (float3(y, c) - offset.xyz) * scale.xyz;
                rgb = saturate(float3(dot(row0.xyz, v), dot(row1.xyz, v), dot(row2.xyz, v)));
            }
            return float4(applyLUT(adjustColor(rgb)), 1);
        }

        float4 PSScale(VSOut i) : SV_Target
        {
            return float4(plane0.Sample(smp, i.uv).rgb, 1);
        }
        """;
}

/// <summary>Mirrors <c>cbuffer Params</c>: 11 float4 + 1 int4 = 192 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShaderParams
{
    public Vector4 Row0, Row1, Row2, Offset, Scale, Adjust, Sharpen, Lut, LutMin, LutMax, UvRect;
    public int Input, Pad0, Pad1, Pad2;

    /// <summary>Same constants as MetalRenderer's YCbCrParams.make.</summary>
    public static ShaderParams Make(bool fullRange, bool bt709)
    {
        var p = new ShaderParams
        {
            Adjust = new Vector4(0, 1, 1, 1),
            LutMax = new Vector4(1, 1, 1, 0),
            UvRect = new Vector4(0, 0, 1, 1),
        };
        if (bt709)
        {
            p.Row0 = new Vector4(1, 0, 1.5748f, 0);
            p.Row1 = new Vector4(1, -0.1873f, -0.4681f, 0);
            p.Row2 = new Vector4(1, 1.8556f, 0, 0);
        }
        else
        {
            p.Row0 = new Vector4(1, 0, 1.402f, 0);
            p.Row1 = new Vector4(1, -0.344136f, -0.714136f, 0);
            p.Row2 = new Vector4(1, 1.772f, 0, 0);
        }
        const float chromaOffset = 128f / 255f;
        if (fullRange)
        {
            p.Offset = new Vector4(0, chromaOffset, chromaOffset, 0);
            p.Scale = new Vector4(1, 1, 1, 0);
        }
        else
        {
            p.Offset = new Vector4(16f / 255f, chromaOffset, chromaOffset, 0);
            p.Scale = new Vector4(255f / 219f, 255f / 224f, 255f / 224f, 0);
        }
        return p;
    }
}
