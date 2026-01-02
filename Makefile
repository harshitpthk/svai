.PHONY: help install uninstall publish svai-install svai-uninstall

# Default install prefix for 'make install'. Override like:
#   make install PREFIX=$$HOME/.local
PREFIX ?= /usr/local
BINDIR ?= $(PREFIX)/bin

# Dotnet local tool install location. Ensure ~/.dotnet/tools is on PATH.
TOOL_PATH ?= $$HOME/.dotnet/tools

help:
	@echo "Targets:"
	@echo "  make publish         Publish Release build to ./out/svai (binary: svai)"
	@echo "  make install         Install symlink to $(BINDIR)/svai (uses install.sh)"
	@echo "  make uninstall       Remove $(BINDIR)/svai (uses uninstall.sh)"
	@echo "  make svai-install    Install svai as a local dotnet tool (repo manifest)"
	@echo "  make svai-uninstall  Uninstall svai dotnet tool (repo manifest)"

publish:
	dotnet publish -c Release -o ./out/svai ./src/StockScreener.Cli/StockScreener.Cli.csproj

install:
	PREFIX=$(PREFIX) BINDIR=$(BINDIR) ./install.sh

uninstall:
	PREFIX=$(PREFIX) BINDIR=$(BINDIR) ./uninstall.sh

# Dotnet tool-style install (from source) using a tool manifest.
# This is convenient for dev machines: it pins the tool version in the repo.
#
# NOTE: Requires 'dotnet tool install --add-source <path>' because we're installing
# from a local packed .nupkg.
svai-install:
	@mkdir -p .config
	@if [ ! -f .config/dotnet-tools.json ]; then dotnet new tool-manifest >/dev/null; fi
	@rm -rf ./out/tool
	@mkdir -p ./out/tool
	dotnet pack -c Release -o ./out/tool ./src/StockScreener.Cli/StockScreener.Cli.csproj
	@dotnet tool install svai --tool-manifest .config/dotnet-tools.json --add-source ./out/tool || \
		dotnet tool update svai --tool-manifest .config/dotnet-tools.json --add-source ./out/tool
	@echo "Installed. Run: dotnet tool run svai --help"

svai-uninstall:
	@dotnet tool uninstall svai --tool-manifest .config/dotnet-tools.json || true
