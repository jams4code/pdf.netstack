# Security policy

PdfNetStack parses untrusted input, so parser robustness is a security property.

## Reporting a vulnerability

Report privately through GitHub: **Security > Report a vulnerability** on this repository.
Please do not open a public issue for crashes, hangs, memory exhaustion or anything that lets a
crafted PDF escape the size and nesting limits.

Include a proof of concept file if you can, the options used, and the .NET version. You will get
an acknowledgement within a week.

## Scope

In scope: crashes or unhandled exceptions from crafted input, unbounded CPU or memory use beyond
the configured limits, output that differs from the input while verification reports success.

Out of scope: high memory use that stays within the documented limits and scales with
`MaxDegreeOfParallelism`, and files the optimizer correctly refuses.

## Running on untrusted input

- Keep `MaxInputBytes` and `MaxDecodedStreamBytes` at values your workers can afford.
- Run batch workers with an OS level memory limit (container limits or job objects).
- Use a `CancellationToken` with a timeout per document.
