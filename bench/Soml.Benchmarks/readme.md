# Benchmarks

Reads and writes the shared [benchmark fixtures](https://github.com/soml-lang/soml/tree/main/benchmark) of the SOML repository, which hold the same data in SOML, JSON, and TOML, with Soml, System.Text.Json, Newtonsoft.Json, and Tomlyn. Each library parses the text into its own document model (`SomlValue`, `JsonDocument`, `JToken`, `TomlTable`), and writes that model back to text.

```sh
dotnet run -c Release --project bench/Soml.Benchmarks -- --filter '*'
```

- `--check` reads and writes each fixture once with each library, to see that they all work.
- `--cpu` measures the CPU time of the process instead of the wall-clock time, for a machine that is busy with other work. It runs the benchmarks in the same process.

The main README's table uses `--filter 'Read.*' 'Write.*' --cpu`, with five warmup iterations and 15 measurement iterations per case.

`--filter '*ObjectEdits*'` measures inserting and removing members at the beginning, middle, and end of an indexed object. `--filter '*StringReading*'` measures literal strings, newline escapes, and Unicode escapes in arrays of one or 64 strings. `--filter '*StringWriting*'` measures literal, quoted, escaped, and Unicode strings. `--filter '*ScalarReading*'` measures arrays of instants, durations, keywords, and hexadecimal integers. `--filter '*ConverterCreation*'` measures deserialization with fresh converter caches. `--filter '*ObjectLookup*'` measures first, last, and missing keys in small and indexed objects. These report allocations as well as time.

`Fixtures` is a copy of `soml/benchmark`. Do not edit it here. To update it, check out the `soml` repository next to this one and run:

```sh
rsync --archive --delete ../soml/benchmark/ bench/Soml.Benchmarks/Fixtures/
```
