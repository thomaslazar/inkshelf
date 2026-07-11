FROM mcr.microsoft.com/devcontainers/dotnet:10.0

# python3: needed by the peon-ping installer.
# (No AOT here, so no clang/zlib. No audio encode, so no ffmpeg.)
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 \
    python3-pip \
    && rm -rf /var/lib/apt/lists/*

# Claude Code CLI (native installer) + peon-ping both install to ~/.local/bin
ENV PATH="/home/vscode/.local/bin:${PATH}"

USER vscode
RUN curl -fsSL https://claude.ai/install.sh | bash
USER root

# peon-ping: install as vscode user so hooks land in /home/vscode/.claude/.
# REMOTE_CONTAINERS tricks the installer into devcontainer mode so it
# skips the audio-player check (audio goes through the relay on the host).
USER vscode
RUN export REMOTE_CONTAINERS=true \
    && curl -fsSL https://peonping.com/install | bash
USER root
