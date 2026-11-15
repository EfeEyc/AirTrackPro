using System;
using System.Collections.Generic;
using System.Text;

namespace AirTrackBridge
{
    public static class QrSvgGenerator
    {
        private static readonly int[] DataCodewordsL = { 0, 19, 34, 55, 80 };
        private static readonly int[] EcCodewordsL = { 0, 7, 10, 15, 20 };
        private static readonly int[] AlignPos = { 0, 0, 18, 22, 26 };

        public static string GenerateSvg(string text, int modulePixels)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text ?? "");
            int version = 0;
            for (int v = 1; v <= 4; v++)
            {
                if (payload.Length + 2 <= DataCodewordsL[v])
                {
                    version = v;
                    break;
                }
            }
            if (version == 0)
            {
                // Truncate safely if an unusually long URL is passed
                version = 4;
                var trimmed = new byte[DataCodewordsL[4] - 2];
                Array.Copy(payload, trimmed, trimmed.Length);
                payload = trimmed;
            }

            int size = version * 4 + 17;
            int dataLen = DataCodewordsL[version];
            int ecLen = EcCodewordsL[version];

            byte[] dataBytes = EncodeDataCodewords(payload, dataLen);
            byte[] ecBytes = ComputeReedSolomon(dataBytes, ecLen);
            var allBits = new List<bool>((dataLen + ecLen) * 8 + 8);
            AppendBytes(allBits, dataBytes);
            AppendBytes(allBits, ecBytes);
            int rem = version >= 2 ? 7 : 0;
            for (int i = 0; i < rem; i++) allBits.Add(false);

            bool[,] isFunc = new bool[size, size];
            bool[,] baseModules = new bool[size, size];
            DrawFunctionPatterns(version, size, baseModules, isFunc);

            // Evaluate all 8 masks and pick the lowest penalty
            int bestMask = 0;
            int bestPenalty = int.MaxValue;
            bool[,] bestGrid = null;

            for (int mask = 0; mask < 8; mask++)
            {
                bool[,] grid = (bool[,])baseModules.Clone();
                PlaceDataBits(size, grid, isFunc, allBits, mask);
                DrawFormatBits(size, grid, mask);
                int p = CalculatePenalty(size, grid);
                if (p < bestPenalty)
                {
                    bestPenalty = p;
                    bestMask = mask;
                    bestGrid = grid;
                }
            }

            int quiet = 4;
            int total = (size + quiet * 2) * modulePixels;
            var sb = new StringBuilder();
            sb.AppendFormat(
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {0} {0}\" shape-rendering=\"crispEdges\">",
                total);
            sb.AppendFormat("<rect width=\"{0}\" height=\"{0}\" fill=\"#ffffff\"/>", total);
            sb.Append("<path fill=\"#0b0f17\" d=\"");
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c < size; c++)
                {
                    if (bestGrid[r, c])
                    {
                        int x = (c + quiet) * modulePixels;
                        int y = (r + quiet) * modulePixels;
                        sb.AppendFormat("M{0},{1}h{2}v{2}h-{2}z", x, y, modulePixels);
                    }
                }
            }
            sb.Append("\"/></svg>");
            return sb.ToString();
        }

        private static byte[] EncodeDataCodewords(byte[] payload, int dataLen)
        {
            var bits = new List<bool>(dataLen * 8);
            // Byte mode indicator: 0100
            AppendBits(bits, 0x4, 4);
            // Character count (8 bits for Versions 1..9)
            AppendBits(bits, payload.Length, 8);
            for (int i = 0; i < payload.Length; i++)
            {
                AppendBits(bits, payload[i], 8);
            }
            // Terminator (up to 4 zero bits)
            int maxBits = dataLen * 8;
            int term = Math.Min(4, maxBits - bits.Count);
            for (int i = 0; i < term; i++) bits.Add(false);
            // Pad to byte boundary
            while (bits.Count % 8 != 0) bits.Add(false);

            var outBytes = new byte[dataLen];
            int idx = 0;
            for (int i = 0; i < bits.Count; i += 8)
            {
                int b = 0;
                for (int k = 0; k < 8; k++)
                {
                    b = (b << 1) | (bits[i + k] ? 1 : 0);
                }
                outBytes[idx++] = (byte)b;
            }

            bool toggle = true;
            while (idx < dataLen)
            {
                outBytes[idx++] = (byte)(toggle ? 0xEC : 0x11);
                toggle = !toggle;
            }
            return outBytes;
        }

        private static byte[] ComputeReedSolomon(byte[] data, int ecLen)
        {
            byte[] gen = BuildGeneratorPoly(ecLen);
            byte[] rem = new byte[ecLen];
            for (int i = 0; i < data.Length; i++)
            {
                byte factor = (byte)(data[i] ^ rem[0]);
                Array.Copy(rem, 1, rem, 0, ecLen - 1);
                rem[ecLen - 1] = 0;
                for (int j = 0; j < ecLen; j++)
                {
                    rem[j] ^= GfMul(gen[j], factor);
                }
            }
            return rem;
        }

        private static byte[] BuildGeneratorPoly(int degree)
        {
            byte[] result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = GfMul(result[j], (byte)root);
                    if (j + 1 < degree)
                    {
                        result[j] ^= result[j + 1];
                    }
                }
                root = GfMul((byte)root, 2);
            }
            return result;
        }

        private static byte GfMul(byte x, byte y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ (((z >> 7) & 1) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        private static void DrawFunctionPatterns(int version, int size, bool[,] grid, bool[,] isFunc)
        {
            // Timing patterns
            for (int i = 0; i < size; i++)
            {
                bool dark = (i % 2) == 0;
                SetFunc(grid, isFunc, 6, i, dark);
                SetFunc(grid, isFunc, i, 6, dark);
            }

            // 3 Finder patterns + separators
            DrawFinder(grid, isFunc, size, 3, 3);
            DrawFinder(grid, isFunc, size, 3, size - 4);
            DrawFinder(grid, isFunc, size, size - 4, 3);

            // Alignment pattern (Versions 2..4 have 1 at (pos, pos))
            if (version >= 2)
            {
                int pos = AlignPos[version];
                for (int dr = -2; dr <= 2; dr++)
                {
                    for (int dc = -2; dc <= 2; dc++)
                    {
                        bool dark = Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1;
                        SetFunc(grid, isFunc, pos + dr, pos + dc, dark);
                    }
                }
            }

            // Format info reservations + fixed dark module
            for (int i = 0; i <= 8; i++)
            {
                isFunc[8, i] = true;
                isFunc[i, 8] = true;
            }
            for (int i = 0; i < 8; i++)
            {
                isFunc[8, size - 1 - i] = true;
                isFunc[size - 1 - i, 8] = true;
            }
            SetFunc(grid, isFunc, size - 8, 8, true);
        }

        private static void DrawFinder(bool[,] grid, bool[,] isFunc, int size, int cr, int cc)
        {
            for (int dr = -4; dr <= 4; dr++)
            {
                for (int dc = -4; dc <= 4; dc++)
                {
                    int r = cr + dr;
                    int c = cc + dc;
                    if (r < 0 || r >= size || c < 0 || c >= size) continue;
                    int dist = Math.Max(Math.Abs(dr), Math.Abs(dc));
                    bool dark = dist != 2 && dist != 4;
                    SetFunc(grid, isFunc, r, c, dark);
                }
            }
        }

        private static void DrawFormatBits(int size, bool[,] grid, int mask)
        {
            // Error correction level L = 01
            int data = (1 << 3) | mask;
            int rem = data;
            for (int i = 0; i < 10; i++)
            {
                rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
            }
            int bits = ((data << 10) | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++)
                grid[8, i] = ((bits >> (14 - i)) & 1) != 0;
            grid[8, 7] = ((bits >> 8) & 1) != 0;
            grid[8, 8] = ((bits >> 7) & 1) != 0;
            grid[7, 8] = ((bits >> 6) & 1) != 0;
            for (int i = 0; i <= 5; i++)
                grid[5 - i, 8] = ((bits >> (5 - i)) & 1) != 0;

            for (int i = 0; i <= 6; i++)
                grid[size - 1 - i, 8] = ((bits >> i) & 1) != 0;
            for (int i = 0; i <= 7; i++)
                grid[8, size - 8 + i] = ((bits >> (7 + i)) & 1) != 0;
        }

        private static void PlaceDataBits(int size, bool[,] grid, bool[,] isFunc, List<bool> bits, int mask)
        {
            int bitIdx = 0;
            bool upward = true;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int step = 0; step < size; step++)
                {
                    int r = upward ? (size - 1 - step) : step;
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int c = right - dx;
                        if (!isFunc[r, c])
                        {
                            bool bit = bitIdx < bits.Count && bits[bitIdx];
                            bitIdx++;
                            bool invert = EvaluateMask(mask, r, c);
                            grid[r, c] = bit ^ invert;
                        }
                    }
                }
                upward = !upward;
            }
        }

        private static bool EvaluateMask(int mask, int r, int c)
        {
            switch (mask)
            {
                case 0: return ((r + c) % 2) == 0;
                case 1: return (r % 2) == 0;
                case 2: return (c % 3) == 0;
                case 3: return ((r + c) % 3) == 0;
                case 4: return ((r / 2 + c / 3) % 2) == 0;
                case 5: return ((r * c) % 2 + (r * c) % 3) == 0;
                case 6: return (((r * c) % 2 + (r * c) % 3) % 2) == 0;
                case 7: return (((r + c) % 2 + (r * c) % 3) % 2) == 0;
                default: return false;
            }
        }

        private static int CalculatePenalty(int size, bool[,] grid)
        {
            int penalty = 0;
            // Rule 1: 5+ consecutive modules in row/col
            for (int r = 0; r < size; r++)
            {
                int runRow = 1;
                int runCol = 1;
                for (int i = 1; i < size; i++)
                {
                    if (grid[r, i] == grid[r, i - 1]) runRow++;
                    else
                    {
                        if (runRow >= 5) penalty += 3 + (runRow - 5);
                        runRow = 1;
                    }
                    if (grid[i, r] == grid[i - 1, r]) runCol++;
                    else
                    {
                        if (runCol >= 5) penalty += 3 + (runCol - 5);
                        runCol = 1;
                    }
                }
                if (runRow >= 5) penalty += 3 + (runRow - 5);
                if (runCol >= 5) penalty += 3 + (runCol - 5);
            }

            // Rule 2: 2x2 blocks
            for (int r = 0; r < size - 1; r++)
            {
                for (int c = 0; c < size - 1; c++)
                {
                    bool v = grid[r, c];
                    if (v == grid[r, c + 1] && v == grid[r + 1, c] && v == grid[r + 1, c + 1])
                    {
                        penalty += 3;
                    }
                }
            }

            // Rule 4: dark ratio
            int dark = 0;
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (grid[r, c]) dark++;
            int total = size * size;
            int k = Math.Abs(dark * 20 - total * 10) / total;
            penalty += k * 10;
            return penalty;
        }

        private static void SetFunc(bool[,] grid, bool[,] isFunc, int r, int c, bool val)
        {
            grid[r, c] = val;
            isFunc[r, c] = true;
        }

        private static void AppendBytes(List<bool> bits, byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
                AppendBits(bits, bytes[i], 8);
        }

        private static void AppendBits(List<bool> bits, int val, int count)
        {
            for (int i = count - 1; i >= 0; i--)
                bits.Add(((val >> i) & 1) != 0);
        }
    }
}
