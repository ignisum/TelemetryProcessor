using System.Text;

namespace TelemetryProcessor
{
    public class TelemetryDataProcessor
    {
        private static readonly int[] SyncPattern = {
         0,0,0,1, 1,0,1,0, 1,1,0,0, 1,1,1,1,
         1,1,1,1, 1,1,0,0, 0,0,0,1, 1,1,0,1};


        public void ProcessFile(string inputFilePath, string outputDir)
        {
            try
            {
                WriteInfo($"Начало обработки: {Path.GetFileName(inputFilePath)}");

                var rawBytes = File.ReadAllBytes(inputFilePath);
                WriteStat($"Прочитано {rawBytes.Length:N0} байт");

                WriteStep("Извлечение каналов");
                var (samples, ch0Count, ch1Count) = ParseRawBytes(rawBytes);
                WriteStat($"Извлечено {samples.Count:N0} отсчётов");
                WriteStat($"Канал 0: {ch0Count:N0} ({(double)ch0Count / samples.Count:P1}), Канал 1: {ch1Count:N0} ({(double)ch1Count / samples.Count:P1})");

                SaveChannelsToCsv(samples, Path.Combine(outputDir, "channels.csv"));
                WriteStat("Каналы сохранены в channels.csv");

                WriteStep("Декодирование сигнала");
                var (decodedBits, syncCount) = DecodeSignal(samples);
                WriteStat($"Декодировано {decodedBits.Count:N0} бит, найдено {syncCount:N0} синхроимпульсов");

                SaveDecodedBitsToCsv(decodedBits, Path.Combine(outputDir, "decoded_bits.csv"));
                WriteStat("Декодированные биты сохранены в decoded_bits.csv");

                WriteStep("Поиск кадров телеметрии");
                var (frames, stats) = FindFrames(decodedBits, outputDir);
                WriteStat(frames.Count > 0
                    ? $"Найдено {frames.Count:N0} кадров. {stats}"
                    : "Кадры не найдены");

                SaveFramesToBinary(frames, Path.Combine(outputDir, "out.bin"));
                WriteStat(frames.Count > 0
                    ? "Кадры сохранены в out.bin"
                    : "Файл out.bin создан (пустой, кадры не найдены)");

                WriteSuccess("Обработка завершена успешно!");
            }
            catch (Exception ex)
            {
                WriteError($"Ошибка: {ex.Message}");
            }
        }

        private (List<(int ch0, int ch1)>, int, int) ParseRawBytes(byte[] bytes)
        {
            var samples = new List<(int, int)>(bytes.Length);
            int ch0Count = 0, ch1Count = 0;

            for (int i = 0; i < bytes.Length; i++)
            {
                if (i % 100000 == 0)
                    Console.Write($"\r[{DateTime.Now:HH:mm:ss}] Извлечение: {i + 1:N0}/{bytes.Length:N0} байт...");

                int ch0 = (bytes[i] >> 0) & 1;
                int ch1 = (bytes[i] >> 1) & 1;
                samples.Add((ch0, ch1));

                ch0Count += ch0;
                ch1Count += ch1;
            }

            Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r");
            return (samples, ch0Count, ch1Count);
        }

        private void SaveChannelsToCsv(List<(int ch0, int ch1)> samples, string outputPath)
        {
            using var writer = new StreamWriter(outputPath);
            for (int i = 0; i < samples.Count; i++)
            {
                if (i % 100000 == 0)
                    Console.Write($"\r[{DateTime.Now:HH:mm:ss}] Запись: {i + 1:N0}/{samples.Count:N0} строк...");

                writer.WriteLine($"{samples[i].ch0},{samples[i].ch1},{samples[i].ch0 & samples[i].ch1}");
            }
            Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r");
        }

        private (List<int>, int) DecodeSignal(List<(int ch0, int ch1)> samples)
        {
            var decodedBits = new List<int>();
            var syncIndexes = new List<int>();

            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i].ch0 == 1 && samples[i].ch1 == 1)
                    syncIndexes.Add(i);
            }

            for (int i = 1; i < syncIndexes.Count; i++)
            {
                int mid = (syncIndexes[i - 1] + syncIndexes[i]) / 2;
                var (ch0, ch1) = samples[mid];

                if (ch1 == 1 && ch0 == 0)
                    decodedBits.Add(1);
                else if (ch0 == 1 && ch1 == 0)
                    decodedBits.Add(0);
                else
                    decodedBits.Add(-1);
            }

            decodedBits = decodedBits.Where(b => b != -1).ToList();

            return (decodedBits, syncIndexes.Count);
        }

        private void SaveDecodedBitsToCsv(List<int> bits, string outputPath)
        {
            using var writer = new StreamWriter(outputPath);
            for (int i = 0; i < bits.Count; i++)
            {
                writer.WriteLine(bits[i]);
            }
        }

        private (List<byte[]>, string) FindFrames(List<int> bits, string outputDir)
        {
            var frames = new List<byte[]>();
            var partialMatches = new List<int>();
            int totalBits = 0;
            int minFrameBits = int.MaxValue;
            int maxFrameBits = 0;

            for (int i = 0; i <= bits.Count - SyncPattern.Length; i++)
            {
                bool isMatch = true;
                for (int j = 0; j < SyncPattern.Length; j++)
                {
                    if (bits[i + j] != SyncPattern[j])
                    {
                        isMatch = false;
                        break;
                    }
                }

                if (isMatch)
                {
                    int frameStart = i;
                    int nextSync = FindNextSync(bits, i + SyncPattern.Length);
                    int frameEnd = nextSync > 0 ? nextSync : bits.Count;
                    int frameLength = frameEnd - frameStart;

                    totalBits += frameLength;
                    minFrameBits = Math.Min(minFrameBits, frameLength);
                    maxFrameBits = Math.Max(maxFrameBits, frameLength);

                    frames.Add(PackBitsToBytes(bits, frameStart, frameLength));
                    i = frameEnd - 1;
                }
            }

            for (int k = 0; k <= bits.Count - 24; k++)
            {
                int matchCount = 0;
                for (int j = 0; j < 24; j++)
                {
                    if (bits[k + j] == SyncPattern[j]) matchCount++;
                }
                if (matchCount >= 20)
                    partialMatches.Add(k);
            }

            if (partialMatches.Count > 0)
            {
                WriteInfo($"Частичных совпадений: {partialMatches.Count}");
            }

            if (frames.Count == 0 && bits.Count > 0)
            {
                try
                {
                    string debugPath = Path.Combine(outputDir, "debug_first_1000_bits.txt");
                    File.WriteAllText(debugPath, string.Join("", bits.Take(1000)));
                    WriteError($"Синхромаркер не найден. Первые 1000 бит сохранены в: {debugPath}");

                    string first32Bits = string.Join("", bits.Take(32));
                    WriteError($"Первые 32 бита: {first32Bits}");

                    if (partialMatches.Count > 0)
                    {
                        string partialPath = Path.Combine(outputDir, "partial_matches.txt");
                        File.WriteAllLines(partialPath,
                            partialMatches.Select(m => $"Позиция: {m}, данные: {string.Join("", bits.Skip(m).Take(32))}"));
                    }
                }
                catch (Exception ex)
                {
                    WriteError($"Ошибка при сохранении отладочной информации: {ex.Message}");
                }
            }

            string stats = frames.Count > 0
                ? $"Длина: {minFrameBits}-{maxFrameBits} бит"
                : "Кадры не найдены. " + (partialMatches.Count > 0 ?
                    $"Есть {partialMatches.Count} частичных совпадений" :
                    "Нет даже частичных совпадений");

            return (frames, stats);
        }

        private int FindNextSync(List<int> bits, int startIndex)
        {
            for (int i = startIndex; i <= bits.Count - SyncPattern.Length; i++)
            {
                bool isMatch = true;
                for (int j = 0; j < SyncPattern.Length; j++)
                {
                    if (bits[i + j] != SyncPattern[j])
                    {
                        isMatch = false;
                        break;
                    }
                }
                if (isMatch) return i;
            }
            return -1;
        }

        private byte[] PackBitsToBytes(List<int> bits, int start, int length)
        {
            byte[] bytes = new byte[(length + 7) / 8];
            for (int i = 0; i < length; i++)
            {
                if (bits[start + i] == 1)
                {
                    int byteIndex = i / 8;
                    int bitPos = 7 - (i % 8);
                    bytes[byteIndex] |= (byte)(1 << bitPos);
                }
            }
            return bytes;
        }

        private void SaveFramesToBinary(List<byte[]> frames, string outputPath)
        {
            using var stream = File.Create(outputPath);
            foreach (var frame in frames)
            {
                stream.Write(frame, 0, frame.Length);
            }
        }

        private void WriteStep(string text)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
            Console.ResetColor();
        }

        private void WriteStat(string text)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
            Console.ResetColor();
        }

        private void WriteError(string text)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
            Console.ResetColor();
        }

        private void WriteSuccess(string text)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
            Console.ResetColor();
        }

        private void WriteInfo(string text)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
            Console.ResetColor();
        }
    }

    class Program
    {
        static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== Телеметрический процессор ===");

            string inputFile = SelectInputFile();
            if (inputFile == null) return;

            string outputDir = CreateOutputDirectory(inputFile);
            var processor = new TelemetryDataProcessor();

            Console.WriteLine("Нажмите Enter, чтобы начать...");
            Console.ReadLine();

            processor.ProcessFile(inputFile, outputDir);

            Console.WriteLine($"Результаты сохранены в: {outputDir}");
        }

        static string SelectInputFile()
        {
            var binFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.bin");
            if (binFiles.Length == 0)
            {
                Console.WriteLine("Файлы .bin не найдены!");
                return null;
            }

            Console.WriteLine("Доступные .bin файлы:");
            for (int i = 0; i < binFiles.Length; i++)
            {
                FileInfo fi = new FileInfo(binFiles[i]);
                Console.WriteLine($"{i + 1}. {fi.Name} ({fi.Length / 1024:N0} KB)");
            }

            Console.Write("Введите номер файла: ");
            if (int.TryParse(Console.ReadLine(), out int choice) && choice > 0 && choice <= binFiles.Length)
                return binFiles[choice - 1];

            Console.WriteLine("Некорректный выбор!");
            return null;
        }

        static string CreateOutputDirectory(string inputFile)
        {
            string dirName = $"{Path.GetFileNameWithoutExtension(inputFile)}_result_{DateTime.Now:yyyyMMdd_HHmmss}";
            string outputDir = Path.Combine(Path.GetDirectoryName(inputFile), dirName);
            Directory.CreateDirectory(outputDir);
            return outputDir;
        }
    }
}