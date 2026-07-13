VERSION     ?= 1.0.0.0
PLUGIN_ASM  := Jellyfin.Plugin.ContentFilter
PLUGIN_DIR  := ContentFilter_$(VERSION)
DIST        := dist
PKG_DIR     := $(DIST)/$(PLUGIN_DIR)
ZIP_FILE    := $(DIST)/$(PLUGIN_ASM)_$(VERSION).zip
PROJECT     := Jellyfin.Plugin.ContentFilter/ContentFilter.csproj

# Override with your Jellyfin plugins path if not using Docker
# e.g. make install JELLYFIN_PLUGINS=/mnt/homelab/jellyfin/plugins
JELLYFIN_PLUGINS ?= $(HOME)/.local/share/jellyfin/plugins

# Portable MD5 checksum helper (macOS vs Linux)
_MD5 = md5 -q $(1) 2>/dev/null || md5sum $(1) | awk '{print $$1}'

.PHONY: all build package install serve docker-up docker-down clean

## Default: build and package
all: package

## Compile the plugin (debug check)
build:
	dotnet build $(PROJECT) -c Release

## Publish + zip into dist/
package:
	@mkdir -p $(PKG_DIR)
	dotnet publish $(PROJECT) \
		-c Release \
		-o $(PKG_DIR) \
		--no-self-contained \
		/p:Version=$(VERSION)
	@# Strip framework DLLs that Jellyfin provides at runtime
	@find $(PKG_DIR) -name "Jellyfin.*.dll" ! -name "$(PLUGIN_ASM).dll" -delete 2>/dev/null || true
	@find $(PKG_DIR) -name "MediaBrowser.*.dll" -delete 2>/dev/null || true
	@find $(PKG_DIR) -name "Microsoft.Extensions.*.dll" -delete 2>/dev/null || true
	@find $(PKG_DIR) -name "*.runtimeconfig.json" -delete 2>/dev/null || true
	@cp meta.json $(PKG_DIR)/meta.json
	@cd $(PKG_DIR) && zip -rq ../../$(ZIP_FILE) . && cd ../..
	@echo ""
	@echo "Package : $(ZIP_FILE)"
	@echo "Checksum: $$($(call _MD5,$(ZIP_FILE)))"
	@echo ""
	@echo "Update manifest.json with the checksum above before publishing."

## Copy plugin directory into your local Jellyfin plugins folder
install: package
	@mkdir -p "$(JELLYFIN_PLUGINS)/$(PLUGIN_DIR)"
	@cp -r $(PKG_DIR)/. "$(JELLYFIN_PLUGINS)/$(PLUGIN_DIR)/"
	@echo "Installed to: $(JELLYFIN_PLUGINS)/$(PLUGIN_DIR)"
	@echo "Restart Jellyfin to load the plugin."

## Serve manifest.json on http://localhost:8765 for local repo testing
serve:
	@echo "Serving plugin repository at http://localhost:8765/manifest.json"
	@echo "Add that URL in Jellyfin: Dashboard > Plugins > Repositories > +"
	python3 -m http.server 8765

## Start local Jellyfin dev instance (requires: make package first)
docker-up: package
	docker compose up -d
	@echo "Jellyfin: http://localhost:8096"

docker-down:
	docker compose down

## Remove build artifacts
clean:
	rm -rf $(DIST) jellyfin-data
