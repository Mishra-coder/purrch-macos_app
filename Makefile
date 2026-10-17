.PHONY: build app run dmg icon clean help

help:            ## List targets
	@grep -E '^[a-z]+:.*##' $(MAKEFILE_LIST) | awk -F':.*## ' '{printf "  %-8s %s\n", $$1, $$2}'

build:           ## Compile (debug)
	swift build

app:             ## Build build/Purrch.app (release)
	scripts/build-app.sh release

run: app         ## Build and launch, replacing a running copy
	-pkill -x Purrch
	open build/Purrch.app

dmg: app         ## Package build/Purrch-<version>.dmg
	scripts/package-dmg.sh

icon:            ## Regenerate Resources/AppIcon.icns from the mascot
	rm -f Resources/AppIcon.icns
	scripts/build-app.sh release

clean:           ## Remove build products and caches
	rm -rf .build build
