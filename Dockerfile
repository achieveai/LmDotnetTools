# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble

ARG DEBIAN_FRONTEND=noninteractive
ARG NODE_MAJOR=22
ARG USERNAME=dev
ARG USER_UID=1001
ARG USER_GID=1001
ARG PLAYWRIGHT_VERSION=1.49.0
ARG CLAUDE_CODE_PACKAGE=@anthropic-ai/claude-code@2.1.114
ARG CLAUDE_AGENT_SDK_PACKAGE=@anthropic-ai/claude-agent-sdk@0.1.55
ARG COPILOT_CLI_PACKAGE=@github/copilot
ARG CODEX_CLI_PACKAGE=@openai/codex
ARG INSTALL_OPTIONAL_COPILOT_SDK=false

ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    NPM_CONFIG_UPDATE_NOTIFIER=false \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    POWERSHELL_TELEMETRY_OPTOUT=1 \
    WORKBENCH_USER=${USERNAME} \
    WORKBENCH_WORKSPACE=/workspace

SHELL ["/bin/bash", "-o", "pipefail", "-c"]

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        apt-transport-https \
        ca-certificates \
        curl \
        docker.io \
        fontconfig \
        fonts-liberation \
        git \
        gnupg \
        jq \
        less \
        libasound2t64 \
        libatk-bridge2.0-0 \
        libatk1.0-0 \
        libatspi2.0-0 \
        libcairo2 \
        libcups2 \
        libdbus-1-3 \
        libdrm2 \
        libgbm1 \
        libglib2.0-0 \
        libgtk-3-0 \
        libnspr4 \
        libnss3 \
        libpango-1.0-0 \
        libpangocairo-1.0-0 \
        libu2f-udev \
        libvulkan1 \
        libx11-6 \
        libx11-xcb1 \
        libxcb1 \
        libxcomposite1 \
        libxdamage1 \
        libxext6 \
        libxfixes3 \
        libxkbcommon0 \
        libxrandr2 \
        libxshmfence1 \
        lsb-release \
        procps \
        python3 \
        python3-pip \
        software-properties-common \
        sudo \
        unzip \
        wget \
        xvfb \
        zip

RUN source /etc/os-release \
    && curl -fsSL "https://packages.microsoft.com/config/ubuntu/${VERSION_ID}/packages-microsoft-prod.deb" -o /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && rm /tmp/packages-microsoft-prod.deb \
    && curl -fsSL "https://deb.nodesource.com/setup_${NODE_MAJOR}.x" | bash - \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        nodejs \
        powershell \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL "https://dot.net/v1/dotnet-install.sh" -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 9.0 --install-dir /usr/share/dotnet \
    && /tmp/dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh

RUN python3 -m pip install --no-cache-dir --break-system-packages uv \
    && npm config --global set prefix /usr/local \
    && npm install -g \
        "${CLAUDE_CODE_PACKAGE}" \
        "${CLAUDE_AGENT_SDK_PACKAGE}" \
        "${COPILOT_CLI_PACKAGE}" \
        "${CODEX_CLI_PACKAGE}" \
    && if [[ "${INSTALL_OPTIONAL_COPILOT_SDK}" == "true" ]]; then npm install -g @github/copilot-sdk; fi \
    && mkdir -p "${PLAYWRIGHT_BROWSERS_PATH}" \
    && npx -y "playwright@${PLAYWRIGHT_VERSION}" install chromium

RUN if ! getent group "${USER_GID}" > /dev/null; then groupadd --gid "${USER_GID}" "${USERNAME}"; fi \
    && useradd --uid "${USER_UID}" --gid "${USER_GID}" --create-home --shell /bin/bash "${USERNAME}" \
    && usermod -aG sudo "${USERNAME}" \
    && echo "${USERNAME} ALL=(ALL) NOPASSWD:ALL" > "/etc/sudoers.d/${USERNAME}" \
    && chmod 0440 "/etc/sudoers.d/${USERNAME}" \
    && chown -R "${USER_UID}:${USER_GID}" /ms-playwright

RUN dotnet --list-sdks \
    && node --version \
    && npm --version \
    && pwsh --version \
    && python3 --version \
    && uv --version \
    && docker --version \
    && claude --version \
    && copilot --version \
    && codex --version \
    && test -f /usr/local/lib/node_modules/@anthropic-ai/claude-agent-sdk/cli.js

COPY scripts/docker-workbench-entrypoint.ps1 /usr/local/bin/docker-workbench-entrypoint.ps1

# RevoBot credential helper — installs the in-container shim and entrypoint
# wedge that fetch GH_TOKEN / AZURE_DEVOPS_EXT_PAT and back git's
# credential.helper from the daemon's loopback proxy. Templates are staged
# from b:/sources/revobot/templates by docker-workbench-build.ps1 into
# .credhelper-staging/ before the build runs.
#
# IMPORTANT: This image is NOT buildable via plain `docker build .`. The
# `.credhelper-staging/` directory is git-ignored and must be populated by
# scripts/docker-workbench-build.ps1, which is the supported entry point.
COPY .credhelper-staging/git-credential-revobot .credhelper-staging/revobot-entrypoint /usr/local/bin/
RUN chmod +x /usr/local/bin/git-credential-revobot /usr/local/bin/revobot-entrypoint \
 && git config --system credential.helper revobot

# Chain `revobot-entrypoint` to the workbench's original entrypoint so the
# `sudo -E -H -u $WORKBENCH_USER --` privilege drop in
# docker-workbench-entrypoint.ps1 still runs after credentials are seeded.
# Without this, the docker `--entrypoint` override replaces the original
# entrypoint and Claude CLI runs as root, which causes
# `--dangerously-skip-permissions` to exit 1 ("cannot be used with root/sudo
# privileges for security reasons"). See revobot/docs/bug-tracker.md B-009.
#
# NOTE: The command below MUST stay in lockstep with the ENTRYPOINT
# instruction further down — if one is changed, update the other or the
# revobot-entrypoint chain silently breaks.
ENV REVOBOT_INNER_ENTRYPOINT="pwsh -NoLogo -File /usr/local/bin/docker-workbench-entrypoint.ps1"

WORKDIR /workspace
# Must match $REVOBOT_INNER_ENTRYPOINT above.
ENTRYPOINT ["pwsh", "-NoLogo", "-File", "/usr/local/bin/docker-workbench-entrypoint.ps1"]
CMD ["pwsh", "-NoLogo"]
