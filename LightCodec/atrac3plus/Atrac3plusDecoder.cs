using System;
using static LightCodec.atrac3plus.Atrac3plusData2;
using static LightCodec.Utils.CodecUtils;
using Atrac3plusData2 = LightCodec.atrac3plus.Atrac3plusData2;
using BitReader = LightCodec.Utils.BitReader;
using FFT = LightCodec.Utils.FFT;

namespace LightCodec.atrac3plus {
    public class Atrac3plusDecoder : ILightCodec {
        private Context ctx;

        public virtual int init(
            int bytesPerFrame,
            int channels,
            int outputChannels,
            int codingMode) {
            ChannelUnit.init();

            ctx = new Context();
            ctx.outputChannels = outputChannels;

            ctx.dsp = new Atrac3plusDsp();

            for (int i = 0; i < ctx.numChannelBlocks; i++) {
                ctx.channelUnits[i] = new ChannelUnit();
                ctx.channelUnits[i].Dsp = ctx.dsp;
            }

            // Initialize IPQF.
            ctx.ipqfDctCtx = new FFT();
            ctx.ipqfDctCtx.mdctInit(5, true, 31.0 / 32768.9);

            // Initialize MDCT.
            ctx.mdctCtx = new FFT();
            ctx.dsp.initImdct(ctx.mdctCtx);

            // Initialize wave synthesis tables/state.
            Atrac3plusDsp.initWaveSynth();

            // Initialize gain compensation.
            ctx.gaincCtx = new Atrac();
            ctx.gaincCtx.initGainCompensation(6, 2);

            return 0;
        }

        public virtual unsafe int decode(
            void* inputAddr,
            int inputLength,
            void* output,
            out int outputLength) {
            outputLength = 0;

            if (ctx == null) {
                return AT3P_ERROR;
            }

            if (inputLength < 0) {
                return AT3P_ERROR;
            }

            if (inputLength == 0) {
                return 0;
            }

            ctx.br = new BitReader(inputAddr, inputLength);

            // ATRAC3+ frame must start with 0.
            if (ctx.br.readBool()) {
                Console.WriteLine("Invalid start bit");
                return AT3P_ERROR;
            }

            /*
             * For the current PSP ATRAC3+ stereo format used by SND0.AT3:
             *
             *   start bit
             *   CH_UNIT_STEREO (01)
             *   one complete stereo channel unit
             *   optional padding / terminator bits
             *
             * The previous implementation kept reading until BitsLeft < 2.
             * That allowed padding bits at the end of a 560-byte frame to be
             * interpreted as another channel unit. This produced:
             *
             *   outputLength = 8192
             * or
             *   outputLength = 16384
             *
             * depending on the final padding bits.
             *
             * For a stereo ATRAC3+ frame, one CH_UNIT_STEREO is sufficient.
             */

            int chBlock = 0;

            // One stereo channel unit contains both output channels.
            if (ctx.br.BitsLeft < 2) {
                return AT3P_ERROR;
            }

            int chUnitId = ctx.br.read(2);

            if (chUnitId == CH_UNIT_TERMINATOR) {
                Console.WriteLine("Unexpected channel-unit terminator");
                return AT3P_ERROR;
            }

            if (chUnitId == CH_UNIT_EXTENSION) {
                Console.WriteLine("Non implemented channel unit extension");
                return AT3P_ERROR;
            }

            if (chBlock >= ctx.channelUnits.Length) {
                Console.WriteLine("Too many channel blocks");
                return AT3P_ERROR;
            }

            if (ctx.channelUnits[chBlock] == null) {
                Console.WriteLine($"channelUnits[{chBlock}] = NULL!");
                return AT3P_ERROR;
            }

            /*
             * The current SND0.AT3 is stereo and therefore the expected
             * channel unit is CH_UNIT_STEREO (1).
             */
            if (ctx.outputChannels == 2 && chUnitId != CH_UNIT_STEREO) {
                Console.WriteLine(
                    $"Invalid stereo channel unit: expected {CH_UNIT_STEREO}, got {chUnitId}");

                return AT3P_ERROR;
            }

            int channelsToProcess = chUnitId + 1;

            ctx.channelUnits[chBlock].BitReader = ctx.br;
            ctx.channelUnits[chBlock].ctx.unitType = chUnitId;
            ctx.channelUnits[chBlock].NumChannels = channelsToProcess;

            int ret = ctx.channelUnits[chBlock].decode();

            if (ret < 0) {
                return ret;
            }

            ctx.channelUnits[chBlock].decodeResidualSpectrum(ctx.samples);

            ctx.channelUnits[chBlock].reconstructFrame(ctx);

            int sampleBytes =
                ATRAC3P_FRAME_SAMPLES *
                ctx.outputChannels *
                sizeof(short);

            writeOutput(
                ctx.outpBuf,
                (short*) output + outputLength / sizeof(short),
                ATRAC3P_FRAME_SAMPLES,
                channelsToProcess,
                ctx.outputChannels);

            outputLength += sampleBytes;

            /*
             * Do NOT decode another channel unit here.
             *
             * The remaining bits belong to frame termination/padding.
             *
             * We intentionally consume/ignore them instead of interpreting
             * the first two padding bits as a new channel-unit ID.
             */
            return inputLength;
        }

        public virtual int NumberOfSamples {
            get {
                return ATRAC3P_FRAME_SAMPLES;
            }
        }
    }
}