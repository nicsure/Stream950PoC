using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Stream950PoC
{
    public enum SerialError
    {
        NO_ERROR = 0,
        GENERAL_ERROR = -1,
        TIMEOUT_ERROR = -2,
        PORT_CLOSED_ERROR = -3,
        EOS_ERROR = -4,
        NO_DATA_ERROR = -5
    }

    public static class Stream950
    {
        public const int BaudRate = 230400;
        public const int TimeOut = 1000;
        public const int BufferSize = 2048;
        public const string AudioFolder = "Audio";
        public const int TIMEOUT_OCCURRED = -1;
        private static readonly byte[] readBuffer = new byte[BufferSize];
        public static string PortName { get; set; } = "COM1";
        public static SerialPort? Port { get; set; } = null;

        // state machine vars
        enum State
        {
            WAITING_FOR_START,
            PACKET_TYPE,
            DATA_LENGTH_BYTE_0,
            DATA_LENGTH_BYTE_1,
            DATA_SAMPLES,
            DATA_SAMPLES_DONE,
            FREQ_BYTE_0,
            FREQ_BYTE_1,
            FREQ_BYTE_2,
            FREQ_BYTE_3,
            CHANNEL_NUMBER_BYTE_0,
            CHANNEL_NUMBER_BYTE_1,
            DESCRIPTION,
            STREAM_INFO_DONE,
            END,
            CONFIRM_END,
        }
        private static State currentState = State.WAITING_FOR_START;
        private static int dataLength = 0;
        private static int frequency = 0;
        private static int channelNumber = 0;
        private static string description = string.Empty;
        private static byte[] audioData = System.Array.Empty<byte>();
        private static int audioDataIndex = 0;
        private static DateTime? streamStartTime = null;
        private static TimeSpan streamDuration = TimeSpan.Zero;
        private static bool firstAudioPacket = false;
        private static int riffHeaderSizeOffset = 0;
        private static int dataChunkHeaderSizeOffset = 0;
        private static System.IO.FileStream? wavFileStream = null;
        private static int dataSize = 0;

        public static void Run(string[] args)
        {
            try
            {
                Directory.CreateDirectory(AudioFolder);
            }
            catch
            {
                Console.WriteLine($"Failed to create audio folder {AudioFolder}");
                return;
            }
            if (args.Length > 0)
            {
                PortName = args[0];
            }
            Console.WriteLine($"Using port: {PortName}");
            if (!OpenPort(BaudRate, TimeOut)) return;
            Task.Run(Port_Listener);
            Console.ReadLine();
            try { Port?.Close(); } catch { }
            Thread.Sleep(500);
        }

        public static void ClosePort(SerialError error)
        {
            if(Port != null)
            {
                if (error == SerialError.NO_ERROR)
                    Console.WriteLine($"Closing port {PortName}.");
                else
                    Console.WriteLine($"Closing port {PortName} due to error {error}.");
            }
            try { Port?.Close(); } catch { }
            try { Port?.Dispose(); } catch { }
            Port = null;
        }

        public static bool OpenPort(int baudRate, int timeout)
        {
            ClosePort(SerialError.NO_ERROR);
            try
            {
                Port = new(PortName, baudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = timeout,
                };
                Port.Open();
                Console.WriteLine($"Port {PortName} opened successfully.\r\n");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to open port {PortName}: {ex.Message}");
                return false;
            }
        }

        public static void Port_Listener()
        {
            var sPort = Port;
            if (sPort == null)
                return;
            int bytesRead;

            while (true)
            {
                try
                {
                    bytesRead = sPort.Read(readBuffer, 0, readBuffer.Length);
                }
                catch (TimeoutException)
                {
                    ProcessByte(TIMEOUT_OCCURRED);
                    continue;
                }
                catch
                {
                    bytesRead = 0;
                }
                if (bytesRead == 0)
                {
                    ClosePort(SerialError.EOS_ERROR);
                    return;
                }
                foreach (var b in readBuffer.Take(bytesRead))
                {
                    ProcessByte(b);
                }
            }
        }

        public static void ProcessAudioData(byte[] audioData)
        {
            if(firstAudioPacket)
            {
                Console.WriteLine($"Processing Audio");
                wavFileStream = BuildWavHeader();
                dataSize = 0;
                firstAudioPacket = false;
            }
            if(wavFileStream != null)
            {
                wavFileStream.Write(audioData, 0, audioData.Length);
                wavFileStream.Flush();
            }
            dataSize += audioData.Length;
        }

        public static void ProcessByte(int b) // state machine for processing bytes
        {
            if (b == TIMEOUT_OCCURRED) // reset state machine
            {
                currentState = State.WAITING_FOR_START;
                return;
            }
            switch (currentState)
            {
                default:
                    currentState = State.WAITING_FOR_START;
                    break;

                // packet type processing
                case State.WAITING_FOR_START:
                    if (b == 0xAA)
                        currentState = State.PACKET_TYPE;
                    break;
                case State.PACKET_TYPE:
                    switch(b)
                    {
                        case 0x60: // audio data
                            dataLength = 0;
                            currentState = State.DATA_LENGTH_BYTE_0;
                            break;
                        case 0x61: // stream start
                            // record local start time for the stream
                            streamStartTime = DateTime.Now;
                            currentState = State.FREQ_BYTE_0;
                            firstAudioPacket = true;
                            break;
                        case 0x62: // stream end
                            currentState = State.END;
                            break;
                        default: // invalid packet type, reset state machine
                            currentState = State.WAITING_FOR_START;
                            break;
                    }
                    break;

                // audio data packet processing
                case State.DATA_LENGTH_BYTE_0:
                    dataLength = b;
                    currentState = State.DATA_LENGTH_BYTE_1;
                    break;
                case State.DATA_LENGTH_BYTE_1:
                    dataLength |= b << 8;
                    currentState = dataLength==0 ? State.DATA_SAMPLES_DONE : State.DATA_SAMPLES;
                    audioData = new byte[dataLength];
                    audioDataIndex = 0;
                    break;
                case State.DATA_SAMPLES:
                    audioData[audioDataIndex++] = (byte)b;
                    if (audioDataIndex >= dataLength)
                        currentState = State.DATA_SAMPLES_DONE;
                    break;
                case State.DATA_SAMPLES_DONE:
                    if (b == 0x55)
                        ProcessAudioData(audioData);
                    currentState = State.WAITING_FOR_START;
                    break;

                // event info packet processing
                case State.FREQ_BYTE_0:
                    frequency = b;
                    currentState = State.FREQ_BYTE_1;
                    break;
                case State.FREQ_BYTE_1:
                    frequency |= b << 8;
                    currentState = State.FREQ_BYTE_2;
                    break;
                case State.FREQ_BYTE_2:
                    frequency |= b << 16;
                    currentState = State.FREQ_BYTE_3;
                    break;
                case State.FREQ_BYTE_3:
                    frequency |= b << 24;
                    currentState = State.CHANNEL_NUMBER_BYTE_0;
                    break;
                case State.CHANNEL_NUMBER_BYTE_0:
                    channelNumber = b;
                    currentState = State.CHANNEL_NUMBER_BYTE_1;
                    break;
                case State.CHANNEL_NUMBER_BYTE_1:
                    channelNumber |= b << 8;
                    description = string.Empty;
                    currentState = State.DESCRIPTION;
                    break;
                case State.DESCRIPTION:
                    description += (char)b;
                    if (description.Length >= 32)
                    {
                        description = description.Trim('\0');
                        currentState = State.STREAM_INFO_DONE;
                    }
                    break;
                case State.STREAM_INFO_DONE:
                    if (b == 0x55)
                    {
                        var startStr = streamStartTime.HasValue ? streamStartTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "unknown";
                        Console.WriteLine($"Stream Info: Frequency={(frequency/100000.0):0.00000}, Channel={channelNumber:000}, Info={description}, Start={startStr}");
                    }
                    currentState = State.WAITING_FOR_START;
                    break;

                // event end packet processing
                case State.END:
                    if (b == 0x55)
                    {
                        if (streamStartTime.HasValue)
                        {
                            streamDuration = DateTime.Now - streamStartTime.Value;
                            var durationStr = $"{streamDuration.TotalSeconds:F1}s"; // tenths of a second
                            Console.WriteLine($"Stream End Received: Confirmed. Duration={durationStr}\r\n");
                        }
                        else
                        {
                            Console.WriteLine($"Stream End Received: Confirmed. Duration=unknown\r\n");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Stream End Received: Not Confirmed\r\n");
                    }
                    // reset start time after end
                    streamStartTime = null;
                    currentState = State.WAITING_FOR_START;
                    if(wavFileStream!=null)
                    {
                        int riffSize = (int)wavFileStream.Position - 8;
                        wavFileStream.Seek(riffHeaderSizeOffset, System.IO.SeekOrigin.Begin);
                        wavFileStream.Write(BitConverter.GetBytes(riffSize), 0, 4);
                        wavFileStream.Seek(dataChunkHeaderSizeOffset, System.IO.SeekOrigin.Begin);
                        wavFileStream.Write(BitConverter.GetBytes(dataSize), 0, 4);
                        wavFileStream.Flush();
                        wavFileStream.Close();
                        wavFileStream.Dispose();
                        wavFileStream = null;
                    }
                    break;
            }
        }

        // Assumes PCM 16-bit little-endian by default at 9600 Hz, mono.
        public static System.IO.FileStream? BuildWavHeader(int sampleRate = 9600, short bitsPerSample = 16, short channels = 1)
        {
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            short blockAlign = (short)(channels * bitsPerSample / 8);

            // Prepare metadata subchunks (INFO list)
            byte[] inamBytes = Encoding.ASCII.GetBytes(description ?? string.Empty);
            var startStr = streamStartTime.HasValue ? streamStartTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "unknown";
            var freqStr = (frequency / 100000.0).ToString("0.00000");
            string comment = $"Info={description}, Frequency={freqStr}, Channel={channelNumber}, Start={startStr}";
            byte[] icmtBytes = Encoding.ASCII.GetBytes(comment);
            byte[] icrdBytes = Encoding.ASCII.GetBytes(startStr);
            string filename = $"Stream_{startStr.Replace(':', '-').Replace(' ', '_')}.wav";

            using var infoContent = new System.IO.MemoryStream();
            // write LIST/INFO only if we have any metadata
            // write the 'INFO' list identifier and then the fields
            void writeInfoField(string tag, byte[] data)
            {
                infoContent.Write(Encoding.ASCII.GetBytes(tag), 0, 4);
                infoContent.Write(BitConverter.GetBytes((int)data.Length), 0, 4);
                if (data.Length > 0) infoContent.Write(data, 0, data.Length);
                if ((data.Length & 1) == 1) infoContent.WriteByte(0);
            }

            // include at least one field even if empty description to preserve structure
            // prepend the 'INFO' type later when writing the LIST chunk
            writeInfoField("INAM", inamBytes);
            writeInfoField("ICMT", icmtBytes);
            writeInfoField("ICRD", icrdBytes);

            byte[] infoFields = infoContent.ToArray();
            int listChunkSize = infoFields.Length + 4; // 4 bytes for the 'INFO' identifier

            System.IO.FileStream fs;
            try
            {
                fs = new System.IO.FileStream(Path.Combine(AudioFolder, filename), System.IO.FileMode.Create, System.IO.FileAccess.Write);
            }
            catch
            {
                Console.WriteLine($"Failed to create WAV file {filename}");
                return null;
            }
            using var bw = new System.IO.BinaryWriter(fs, Encoding.ASCII, true);

            // RIFF header
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            // Placeholder for overall size (to be updated later by caller if desired)
            bw.Flush();
            riffHeaderSizeOffset = (int)fs.Position;
            bw.Write((int)0);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));

            // fmt chunk
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write((int)16); // PCM fmt chunk size
            bw.Write((short)1); // audio format = PCM
            bw.Write(channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write(bitsPerSample);

            // LIST/INFO chunk (optional)
            if (listChunkSize > 4)
            {
                bw.Write(Encoding.ASCII.GetBytes("LIST"));
                bw.Write(listChunkSize);
                bw.Write(Encoding.ASCII.GetBytes("INFO"));
                bw.Write(infoFields);
            }

            // data chunk header with placeholder size
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Flush();
            dataChunkHeaderSizeOffset = (int)fs.Position;
            bw.Write((int)0);

            bw.Flush();
            return fs;
        }

    }
}
