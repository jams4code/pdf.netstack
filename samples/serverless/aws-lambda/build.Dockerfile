# Build environment for the Native AOT bootstrap: Amazon Linux 2023, as the provided.al2023
# runtime, so the executable links against the same glibc it runs with. Build it on (or emulated
# as) the architecture you deploy. The .NET SDK tarball is checked against the SHA-512 that the
# .NET download page lists for it; pass both as build arguments.
FROM public.ecr.aws/amazonlinux/amazonlinux:2023

ARG DOTNET_SDK_VERSION
ARG DOTNET_SDK_SHA512

RUN dnf install -y clang zlib-devel tar gzip findutils && dnf clean all

RUN test -n "$DOTNET_SDK_VERSION" && test -n "$DOTNET_SDK_SHA512" \
    || { echo "Pass --build-arg DOTNET_SDK_VERSION=... and --build-arg DOTNET_SDK_SHA512=..."; exit 1; }; \
    arch=$(uname -m | sed 's/aarch64/arm64/; s/x86_64/x64/') \
    && curl -fsSL -o /tmp/sdk.tar.gz "https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/dotnet-sdk-${DOTNET_SDK_VERSION}-linux-${arch}.tar.gz" \
    && echo "${DOTNET_SDK_SHA512}  /tmp/sdk.tar.gz" | sha512sum -c - \
    && mkdir -p /usr/share/dotnet \
    && tar -xzf /tmp/sdk.tar.gz -C /usr/share/dotnet \
    && rm /tmp/sdk.tar.gz \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
