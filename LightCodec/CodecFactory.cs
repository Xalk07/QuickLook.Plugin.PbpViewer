using LightCodec.atrac3;
using LightCodec.atrac3plus;

namespace LightCodec
{
    public enum AudioCodec
    {
        AT3 = 0,
        AT3plus = 1,
        NULL = 4
    }

    public class NullCodec : ILightCodec
    {
        public int NumberOfSamples => 0;

        public int init(int bytesPerFrame, int channels, int outputChannels, int codingMode)
        {
            return -1;
        }

        public unsafe int decode(void* inputAddr, int inputLength, void* output, out int outputLength)
        {
            outputLength = 0;
            return 0;
        }
    }

    public class CodecFactory
    {
        public static ILightCodec Get(AudioCodec codecType)
        {
            switch (codecType)
            {
                case AudioCodec.AT3plus:
                    return new Atrac3plusDecoder();
                case AudioCodec.AT3:
                    return new Atrac3Decoder();
                default:
                    return new NullCodec();
            }
        }
    }
}