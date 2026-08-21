namespace VysorClient.Services;

// Codec G.711 μ-law: transforma cada amostra de 16 bits (2 bytes) em 1 byte só.
// É um algoritmo antigo (telefonia) mas muito leve pra codificar/decodificar em
// tempo real sem gastar CPU, e já reduz o tráfego de áudio pela metade — ajuda
// a rede não travar quando já estamos mandando os frames de vídeo também.
//
// Implementação padrão de referência (ITU-T G.711), usada há décadas em
// bibliotecas de telefonia — não depende de nenhuma biblioteca externa.
public static class MuLawCodec
{
    private const int Bias = 0x84;
    private const int Clip = 32635;

    public static byte Encode(short sample)
    {
        int sign = (sample >> 8) & 0x80;
        // Note o "-(int)sample" em vez de "(short)(-sample)": o menor valor
        // possível de um short (-32768) não tem oposto dentro de um short,
        // então o cast antigo devolvia -32768 de novo (estouro silencioso).
        // O valor seguia negativo, escapava do corte abaixo e saía
        // codificado como silêncio absoluto — um "clique" audível toda vez
        // que o áudio batia no volume máximo.
        int val = sign != 0 ? -(int)sample : sample;
        if (val > Clip) val = Clip;
        val += Bias;

        int exponent = 7;
        for (int expMask = 0x4000; (val & expMask) == 0 && exponent > 0; expMask >>= 1)
            exponent--;

        int mantissa = (val >> (exponent + 3)) & 0x0F;
        int ulawByte = ~(sign | (exponent << 4) | mantissa);
        return (byte)ulawByte;
    }

    public static short Decode(byte ulaw)
    {
        int u = ~ulaw & 0xFF;
        int sign = u & 0x80;
        int exponent = (u >> 4) & 0x07;
        int mantissa = u & 0x0F;

        int sample = ((mantissa << 3) + Bias) << exponent;
        sample -= Bias;

        return (short)(sign != 0 ? -sample : sample);
    }

    public static byte[] EncodeBuffer(short[] samples, int count)
    {
        var result = new byte[count];
        for (int i = 0; i < count; i++)
            result[i] = Encode(samples[i]);
        return result;
    }

    // Decodifica direto para bytes PCM16 little-endian (formato que o
    // BufferedWaveProvider do NAudio espera).
    public static byte[] DecodeToPcm16(byte[] ulawBytes, int offset, int count)
    {
        var pcm = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            short s = Decode(ulawBytes[offset + i]);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return pcm;
    }
}
