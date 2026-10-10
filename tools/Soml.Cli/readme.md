# soml

> Convert between [SOML](https://github.com/soml-lang/soml) and the tagged JSON of its conformance suite

```sh
dotnet tool install --global Soml.Cli
```

```sh
soml decode < config.soml > config.json
soml encode < config.json > config.soml
```

`decode` reads a SOML document and writes it as tagged JSON, where each scalar is `{"type": "int", "value": "1"}` and the value is in canonical form. `encode` does the reverse and writes the document in canonical form. A test harness for the [conformance suite](https://github.com/soml-lang/soml/tree/main/conformance) can run these commands.

The exit code is 0 for success, 1 for an input that is not valid or an I/O error, with the error on standard error, and 64 for invalid arguments. Control characters and bidirectional controls in an error are escaped, like `\u{1b}`, because an error can quote the input and must not send escape sequences to the terminal.
