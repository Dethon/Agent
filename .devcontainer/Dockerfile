FROM mcr.microsoft.com/dotnet/sdk:10.0

# Install system dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    jq \
    vim \
    sudo \
    ca-certificates \
    gnupg \
    && rm -rf /var/lib/apt/lists/*

# Install Docker CLI + Compose plugin (auto-detect distro: ubuntu or debian)
RUN install -m 0755 -d /etc/apt/keyrings \
    && . /etc/os-release \
    && curl -fsSL "https://download.docker.com/linux/$ID/gpg" | gpg --dearmor -o /etc/apt/keyrings/docker.gpg \
    && chmod a+r /etc/apt/keyrings/docker.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/$ID $VERSION_CODENAME stable" > /etc/apt/sources.list.d/docker.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends docker-ce-cli docker-compose-plugin \
    && rm -rf /var/lib/apt/lists/*

# Install .NET workloads (wasm-tools for Blazor WebAssembly)
RUN dotnet workload update --skip-sign-check

# Create non-root user with sudo
RUN useradd -m -s /bin/bash devuser \
    && echo "devuser ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/devuser \
    && mkdir -p /home/devuser/.nuget/packages \
    && chown -R devuser:devuser /home/devuser

# Install Claude Code natively
USER devuser
RUN curl -fsSL https://claude.ai/install.sh | bash
USER root

# Copy and set entrypoint (before USER switch, strip Windows CRLF)
COPY .devcontainer/entrypoint.sh /entrypoint.sh
RUN sed -i 's/\r$//' /entrypoint.sh && chmod +x /entrypoint.sh

USER devuser
WORKDIR /workspace

# Set environment
ENV DOCKER_HOST=tcp://dind:2375
ENV NUGET_PACKAGES=/home/devuser/.nuget/packages
ENV DEVCONTAINER=1
ENV PATH="/home/devuser/.local/bin:${PATH}"

ENTRYPOINT ["/entrypoint.sh"]
