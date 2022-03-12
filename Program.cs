T? GetArg<T>(params string[] name)
{
    var pair = args.SkipWhile(arg => !arg.StartsWith("-") || !name.Contains(arg.TrimStart('-'), StringComparer.OrdinalIgnoreCase)).Take(2).ToArray();
    return pair.Length < 2 || pair[1].StartsWith("-")
        ? default(T?)
        : (T?)System.ComponentModel.TypeDescriptor.GetConverter(typeof(T)).ConvertFrom(pair[1]);
}

static void WaitForKey()
{
    Console.WriteLine("Press any key to exit");
    while (Console.ReadKey().KeyChar == 0) { } // Ignore 0, which includes keys like Win and arrow keys.
}

AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    Console.WriteLine(((Exception)e.ExceptionObject).Message);
    WaitForKey();
};

Console.OutputEncoding = System.Text.Encoding.UTF8; // To allow the Ω symbol.
var stopwatch = System.Diagnostics.Stopwatch.StartNew();

var samplesPerSecond = GetArg<float?>("samplerate", "s") ?? 200e3f;
var resistance = GetArg<float?>("resistance", "r") ?? 10f;
var minFrequency = GetArg<float?>("minfrequency", "f1") ?? 10f;
var maxFrequency = GetArg<float?>("maxfrequency", "f2") ?? 30e3f;
var headerLines = GetArg<int?>("header", "h") ?? 3;
// Use half of the available CPU threads by default because there's no benefit from SMT here.
// This assumes that the CPU actually has SMT. It's possible to check the number of physical cores instead, but only in an OS-specific way.
var threads = GetArg<int?>("threads", "t") ?? Environment.ProcessorCount / 2;
var inputFile = GetArg<string?>("in", "i");
var outpuFile = GetArg<string?>("out", "o") ?? "out.csv";

if (!File.Exists(inputFile))
{
    throw new FileNotFoundException("Input file not found", inputFile);
}

if (Path.GetDirectoryName(outpuFile) == "")
{
    outpuFile = Path.Combine(Path.GetDirectoryName(inputFile)!, outpuFile);
}

// Split the file into batches for two reasons:
// 1) So they can be processed in parallel, for increased performance.
// 2) So the length of the median filter can be varied to best filter noise from a smaller range of frequencies.
var allLines = File.ReadLines(inputFile).Skip(headerLines);
var lineCount = allLines.Count(); // Unfortunately it's necessary to read the entire file to count the number of lines. Fortunately it's pretty quick with an SSD.
var frequencyDecades = MathF.Log10(maxFrequency) - MathF.Log10(minFrequency);
var batchesPerDecade = 5;
var batchCount = (int)(frequencyDecades * batchesPerDecade);
var linesPerBatch = lineCount / batchCount;
var decadesPerBatch = frequencyDecades / batchCount;

Console.WriteLine($"Splitting {lineCount} lines into {batchCount} batches of {linesPerBatch}, to be processed up to {threads} in parallel");
Console.WriteLine();

var batches = new List<(IEnumerable<string> lines, float min, float max, int i)>(batchCount);
for (int i = 0; i < batchCount; i++)
{
    var max = MathF.Pow(10, (decadesPerBatch * (i + 1)) + MathF.Log10(minFrequency));
    var min = MathF.Pow(10, (decadesPerBatch * i) + MathF.Log10(minFrequency));
    var minFrequencyLines = (int)Math.Round(samplesPerSecond / min);
    var maxFrequencyLines = (int)Math.Round(samplesPerSecond / max);
    var batchLines = allLines
        // Add some extra lines so that batches overlap, otherwise the first and last cycles would be incomplete, and thus missing from the final result.
        .Skip((i * linesPerBatch) - minFrequencyLines)
        .Take(linesPerBatch + minFrequencyLines + maxFrequencyLines);
    batches.Add((batchLines, min, max, i));
}

using var sem = new SemaphoreSlim(threads); // To limit the number of batches that are processed at once.
var impedance = batches.AsParallel().SelectMany(batch =>
    {
        sem.Wait();
        Console.WriteLine($"Batch {batch.i}, {(int)batch.min}-{(int)batch.max}Hz");
        var result = CalculateImpedance(batch.lines, samplesPerSecond, batch.max, resistance);
        sem.Release();
        return result;
    })
    // Round frequency to 3 sig. figs. to keep the number of data points reasonable.
    // Have to parse back to float to ensure the result isn't in scientific notation.
    .GroupBy(x => float.Parse(x.f.ToString("G3")))
    // Round impedance to 4 sig. figs. because anything more than that is meaningless.
    .Select(group => (f: group.Key, z: float.Parse(group.Average(x => x.z).ToString("G4"))))
    .OrderBy(x => x.f)
    .ToList();

Console.WriteLine();
Console.WriteLine($"Frequencies: {impedance.Count()}");
Console.WriteLine($"First: {impedance.First().z}Ω @ {impedance.First().f}Hz");
Console.WriteLine($"Last: {impedance.Last().z}Ω @ {impedance.Last().f}Hz");
Console.WriteLine();

var output = impedance.Select(x => $"{x.f},{x.z}").Prepend("Frequency,Impedance");
File.WriteAllLines(outpuFile, output);

Console.WriteLine($"Completed in {stopwatch.Elapsed.ToString("m'm 's\\.ff's'")}");
WaitForKey();

// Try to find complete cycles by detecting zero crossings. The frequency can then be trivially found from the length of the cycle, and the impedance from the amplitude.
// Though the two channels will be out of phase if Z is reactive, it doesn't matter because the frequency is the same for both channels, and thus both will be exactly one cycle long.
static ICollection<(float f, float z)> CalculateImpedance(IEnumerable<string> lines, float samplesPerSecond, float maxFrequency, float resistance)
{
    static float Median(float[] arr, float[] temp)
    {
        Array.Copy(arr, temp, arr.Length);
        Array.Sort(temp);
        return temp[arr.Length / 2];
    }

    var minSamples = (int)Math.Round(samplesPerSecond / maxFrequency);
    // bufferSize needs to be odd so that the median is the middle sample.
    // Set it relative to the frequency, so all frequencies get as much noise reduction as possible.
    var bufferSize = Math.Max(3, ((minSamples / 512) * 2) + 1);
    var medianBufferA = new float[bufferSize];
    var medianBufferB = new float[bufferSize];
    var tempBuffer = new float[bufferSize];
    var index = 0;

    var cycleBuffer = new List<(float vR, float vZ)>();
    var firstHalf = true;
    var expectedEnd = 0;

    var impedance = new List<(float f, float z)>();

    foreach (var line in lines)
    {
        var parts = line.Split(',');
        var a = float.Parse(parts[1]);
        var b = float.Parse(parts[2]);
        medianBufferA[index] = a;
        medianBufferB[index] = b;
        index = (index + 1) % bufferSize;

        var filteredA = Median(medianBufferA, tempBuffer);
        var filteredB = Median(medianBufferB, tempBuffer);

        var positive = filteredB > 0;
        if (firstHalf)
        {
            // Make sure to wait a bit before detecting zero-crossing, otherwise noise could trigger it immediately.
            if (!positive && cycleBuffer.Count > minSamples / 2)
            {
                firstHalf = false;
                expectedEnd = (cycleBuffer.Count * 9) / 10;
            }
        }
        else
        {
            // Now the length of a half-cycle is known, zero-crossing detection can be delayed until where it's expected, for better noise rejection.
            if (positive && cycleBuffer.Count > expectedEnd)
            {
                var vRRms = MathF.Sqrt(cycleBuffer.Sum(sample => sample.vR * sample.vR) / cycleBuffer.Count);
                var vZRms = MathF.Sqrt(cycleBuffer.Sum(sample => sample.vZ * sample.vZ) / cycleBuffer.Count);
                var i = vRRms / resistance;
                var z = vZRms / i;
                var f = samplesPerSecond / cycleBuffer.Count;
                impedance.Add((f, z));
                cycleBuffer.Clear();
                firstHalf = true;
            }
        }

        cycleBuffer.Add((vR: filteredA, vZ: filteredB - filteredA));
    }

    return impedance
        // It's likely that the first and last cycles are incomplete, and will be invalid.
        // Even if they are complete in the source data, the median filter will invalidate the first and last few samples.
        .Skip(1).SkipLast(1)
        .ToList();
}
