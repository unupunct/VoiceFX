using System.Diagnostics;
using System.Numerics;
using VoiceFX.Core;
using VoiceFX.Core.Audio;
using VoiceFX.Core.Dsp;

// Offline DSP verification + optional live-device smoke test.
//   VoiceFX.SelfTest               -> DSP tests
//   VoiceFX.SelfTest --live [outSubstring] [seconds] -> run the real WASAPI pipeline
if (args.Contains("--live")) return Live.Run(args);
return Dsp.Run();

static class Dsp
{
    const int SR = 48000;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fails++; }

    /// <summary>Voice-like test signal: harmonic source (f0) shaped by formants and a syllabic envelope.</summary>
    public static float[] Voice(double seconds, double f0 = 130, bool syllables = true)
    {
        int n = (int)(seconds * SR); var x = new float[n];
        var f1 = new Biquad(); f1.BandPass(SR, 700, 4); var f2 = new Biquad(); f2.BandPass(SR, 1200, 5); var f3 = new Biquad(); f3.BandPass(SR, 2600, 6);
        var rnd = new Random(1); double ph = 0;
        for (int i = 0; i < n; i++)
        {
            ph += f0 / SR; if (ph >= 1) ph -= 1;
            float src = (float)(2 * ph - 1) * 0.6f + (float)(rnd.NextDouble() - 0.5) * 0.02f;
            float y = f1.Process(src) * 3f + f2.Process(src) * 2f + f3.Process(src) * 1.5f;
            double env = syllables ? 0.5 + 0.5 * Math.Sin(2 * Math.PI * 3.5 * i / SR) : 1;
            x[i] = (float)(y * env * 0.5);
        }
        return x;
    }

    public static float[] Run(AudioProcessingEngine e, float[] input, int block = 480, Action<int>? atBlock = null)
    {
        var o = (float[])input.Clone();
        for (int p = 0, b = 0; p < o.Length; p += block, b++)
        {
            atBlock?.Invoke(b);
            e.Process(o.AsSpan(p, Math.Min(block, o.Length - p)));
        }
        return o;
    }

    static double Rms(float[] x, int from = 0) { double s = 0; for (int i = from; i < x.Length; i++) s += x[i] * x[i]; return Math.Sqrt(s / (x.Length - from)); }
    static float Peak(float[] x) { float m = 0; foreach (var v in x) m = Math.Max(m, Math.Abs(v)); return m; }

    static double[] Spectrum(float[] x, int from)
    {
        const int N = 4096; var acc = new double[N / 2]; int cnt = 0;
        for (int p = from; p + N <= x.Length; p += N / 2)
        {
            var c = new Complex[N];
            for (int i = 0; i < N; i++) c[i] = x[p + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
            Fft(c);
            for (int i = 0; i < N / 2; i++) acc[i] += c[i].Magnitude; cnt++;
        }
        for (int i = 0; i < acc.Length; i++) acc[i] /= Math.Max(1, cnt);
        return acc;
    }
    static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++) { int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) (a[i], a[j]) = (a[j], a[i]); }
        for (int len = 2; len <= n; len <<= 1)
        {
            var w = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (int i = 0; i < n; i += len) { Complex cw = 1; for (int j = 0; j < len / 2; j++) { var u = a[i + j]; var v = a[i + j + len / 2] * cw; a[i + j] = u + v; a[i + j + len / 2] = u - v; cw *= w; } }
        }
    }
    static double Centroid(double[] s) { double a = 0, b = 0; for (int i = 1; i < s.Length; i++) { double f = i * (double)SR / (2 * s.Length); a += f * s[i]; b += s[i]; } return a / b; }
    // log-spectrum distance (dB RMS) over 100 Hz - 8 kHz, level-normalised
    static double SpecDist(double[] a, double[] b)
    {
        int lo = (int)(100.0 / SR * 2 * a.Length), hi = (int)(8000.0 / SR * 2 * a.Length);
        double ma = 0, mb = 0; for (int i = lo; i < hi; i++) { ma += 20 * Math.Log10(a[i] + 1e-9); mb += 20 * Math.Log10(b[i] + 1e-9); }
        ma /= hi - lo; mb /= hi - lo; double d = 0;
        for (int i = lo; i < hi; i++) { double x = 20 * Math.Log10(a[i] + 1e-9) - ma - (20 * Math.Log10(b[i] + 1e-9) - mb); d += x * x; }
        return Math.Sqrt(d / (hi - lo));
    }

    static double F0(float[] x, int from, int len, double minHz = 60, double maxHz = 400)
    {
        int lagMin = (int)(SR / maxHz), lagMax = (int)(SR / minHz); double best = -1; int bl = lagMin;
        for (int lag = lagMin; lag <= lagMax; lag++)
        {
            double s = 0, e1 = 0, e2 = 0;
            for (int i = from; i < from + len; i++) { s += x[i] * x[i + lag]; e1 += x[i] * x[i]; e2 += x[i + lag] * x[i + lag]; }
            double r = s / Math.Sqrt(e1 * e2 + 1e-12); if (r > best) { best = r; bl = lag; }
        }
        return (double)SR / bl;
    }

    public static int Run()
    {
        Console.WriteLine("VoiceFX DSP self-test @ 48 kHz\n");
        var presets = new VoicePresetManager(); var meters = new AudioMeterService();
        var voice = Voice(5.0);
        var outs = new float[6][]; var spec = new double[6][];

        Console.WriteLine("1. Per-preset sanity, loudness and CPU");
        for (int p = 0; p < 6; p++)
        {
            var eng = new AudioProcessingEngine(presets, meters) { PresetIndex = p };
            eng.Prepare(SR);
            var sw = Stopwatch.StartNew(); var o = Run(eng, voice); sw.Stop();
            outs[p] = o; spec[p] = Spectrum(o, SR);
            double cpu = sw.Elapsed.TotalSeconds / 5.0 * 100;
            bool finite = o.All(float.IsFinite);
            double rmsDb = 20 * Math.Log10(Rms(o, SR) + 1e-9);
            Console.WriteLine($"  {presets.Presets[p].Name,-11} peak {Peak(o):F2}  rms {rmsDb,6:F1} dBFS  centroid {Centroid(spec[p]),6:F0} Hz  cpu {cpu:F2}% of one core  latency {presets.Presets[p].LatencySamples * 1000.0 / SR:F1} ms");
            Check(finite && Peak(o) <= 0.991f, $"{presets.Presets[p].Name}: finite, never clips");
            Check(rmsDb > -40 && rmsDb < -3, $"{presets.Presets[p].Name}: audible & sane level");
            Check(cpu < 5, $"{presets.Presets[p].Name}: CPU < 5% of a core");
        }

        Console.WriteLine("\n2. Presets are audibly distinct (log-spectrum distance, dB RMS)");
        for (int a = 0; a < 6; a++) for (int b = a + 1; b < 6; b++)
        {
            double d = SpecDist(spec[a], spec[b]);
            Check(d > 1.5, $"{presets.Presets[a].Name} vs {presets.Presets[b].Name}: {d:F1} dB apart");
        }

        Console.WriteLine("\n3. Pitch shift accuracy on a steady 130 Hz vowel (speed is preserved by design)");
        var steady = Voice(3, 130, false);
        foreach (var (idx, expect, name) in new[] { (1, 130 * Math.Pow(2, -3.5 / 12), "Deep radio (-3.5 st)"), (5, 130 * Math.Pow(2, 6 / 12.0), "Alien (+6 st, no warble)") })
        {
            presets.Presets[5].SetValue(2, 0f); // warble depth 0 for the measurement
            var eng = new AudioProcessingEngine(presets, meters) { PresetIndex = idx }; eng.Prepare(SR);
            var o = Run(eng, steady);
            // Measure on the shifted signal's low band; ignore warm-up.
            double f0 = F0(o, SR, 4096, 60, 330);
            Check(Math.Abs(f0 / expect - 1) < 0.06, $"{name}: expected {expect:F1} Hz, measured {f0:F1} Hz");
        }
        presets.ResetAll();

        Console.WriteLine("\n4. Glitch-free preset switching (no engine restart)");
        {
            var sine = new float[SR * 2]; for (int i = 0; i < sine.Length; i++) sine[i] = 0.3f * MathF.Sin(2 * MathF.PI * 200 * i / SR);
            var eng = new AudioProcessingEngine(presets, meters) { PresetIndex = 0 }; eng.Prepare(SR);
            int sw = 0;
            var o = Run(eng, sine, 480, b => { if (b % 20 == 10) { eng.PresetIndex = (b / 20 + 1) % 6; sw++; } });
            float maxJump = 0; for (int i = SR / 2; i < o.Length; i++) maxJump = Math.Max(maxJump, Math.Abs(o[i] - o[i - 1]));
            Console.WriteLine($"  {sw} switches, largest sample-to-sample step {maxJump:F3}");
            Check(maxJump < 0.5f && o.All(float.IsFinite) && Peak(o) <= 0.991f, "switching cycles through all presets without discontinuities or clipping");
        }

        Console.WriteLine("\n5. Limiter, mute and bypass");
        {
            var loud = new float[SR]; for (int i = 0; i < loud.Length; i++) loud[i] = 4f * MathF.Sign(MathF.Sin(2 * MathF.PI * 300 * i / SR));
            var eng = new AudioProcessingEngine(presets, meters) { PresetIndex = 0, InputGainDb = 12 }; eng.Prepare(SR);
            var o = Run(eng, loud);
            Check(Peak(o) <= 0.991f, $"limiter holds full-scale +24 dB input to {Peak(o):F3}");
            var tone = Voice(2);
            eng = new AudioProcessingEngine(presets, meters) { PresetIndex = 0, Muted = true }; eng.Prepare(SR);
            o = Run(eng, tone); Check(Rms(o, SR / 2) < 1e-4, "mute silences output");
            eng = new AudioProcessingEngine(presets, meters) { PresetIndex = 4, Bypass = true }; eng.Prepare(SR);
            o = Run(eng, tone);
            double dIn = Rms(tone, SR / 2), dOut = Rms(o, SR / 2);
            Check(Math.Abs(20 * Math.Log10(dOut / dIn)) < 1.0, $"bypass returns the untouched mic level ({20 * Math.Log10(dOut / dIn):F2} dB difference)");
            eng.Bypass = false; // robot ring-mod must be audibly different from bypass
            var o2 = Run(eng, tone);
            Check(SpecDist(Spectrum(o, SR), Spectrum(o2, SR)) > 1.5, "bypass off re-engages the effect");
        }

        Console.WriteLine("\n6. Parameter extremes stay stable");
        {
            bool ok = true;
            for (int p = 0; p < 6; p++) foreach (var corner in new[] { 0, 1 })
            {
                foreach (var eff in presets.Presets) for (int k = 0; k < eff.Params.Length; k++) eff.SetValue(k, corner == 0 ? eff.Params[k].Min : eff.Params[k].Max);
                var eng = new AudioProcessingEngine(presets, meters) { PresetIndex = p, Intensity = 1f }; eng.Prepare(SR);
                var o = Run(eng, Voice(2)); ok &= o.All(float.IsFinite) && Peak(o) <= 0.991f;
            }
            presets.ResetAll();
            Check(ok, "all six presets at min/max of every parameter: finite and limited");
        }

        Console.WriteLine($"\n{(fails == 0 ? "ALL TESTS PASSED" : fails + " TEST(S) FAILED")}");
        return fails == 0 ? 0 : 1;
    }
}

static class Live
{
    public static int Run(string[] args)
    {
        string outMatch = args.SkipWhile(a => a != "--live").Skip(1).FirstOrDefault() ?? "";
        int secs = int.TryParse(args.SkipWhile(a => a != "--live").Skip(2).FirstOrDefault(), out var s) ? s : 6;
        var log = new DiagnosticsService(); var devs = new AudioDeviceManager(log);
        Console.WriteLine("Inputs:");  foreach (var d in devs.GetInputs()) Console.WriteLine($"  {d.Name}{(d.IsDefault ? " [default]" : "")}");
        Console.WriteLine("Outputs:"); foreach (var d in devs.GetOutputs()) Console.WriteLine($"  {d.Name}{(d.IsDefault ? " [default]" : "")}{(d.IsVirtual ? " [virtual]" : "")}");
        var vdev = devs.FindVirtualOutput(); Console.WriteLine("Virtual cable: " + (vdev?.Name ?? "NOT INSTALLED"));
        var outDev = devs.GetOutputs().FirstOrDefault(d => outMatch.Length > 0 && d.Name.Contains(outMatch, StringComparison.OrdinalIgnoreCase)) ?? vdev;
        var presets = new VoicePresetManager(); using var pipe = new AudioPipeline(devs, presets, log);
        pipe.StateChanged += (st, m) => Console.WriteLine($"  state: {st} - {m}");
        pipe.Processing.Muted = args.Contains("--mute");
        pipe.Start(new PipelineConfig(null, outDev?.Id, 20));
        for (int i = 0; i < secs; i++)
        {
            pipe.Processing.PresetIndex = i % 6;
            Thread.Sleep(1000);
            var (inp, outp, clip) = pipe.Meters.Read();
            Console.WriteLine($"  t={i + 1}s preset={presets.Presets[i % 6].Name,-10} in={inp:F3} out={outp:F3} underruns={pipe.Underruns} est.latency={pipe.EstimatedLatencyMs:F1} ms");
        }
        pipe.Stop();
        Console.WriteLine("Stopped cleanly.");
        return 0;
    }
}
