PREFIX   ?= /usr
DESTDIR  ?=
PKGNAME   = subs2srs
LIBDIR    = $(DESTDIR)$(PREFIX)/lib/$(PKGNAME)
BINDIR    = $(DESTDIR)$(PREFIX)/bin
APPDIR    = $(DESTDIR)$(PREFIX)/share/applications
LICDIR    = $(DESTDIR)$(PREFIX)/share/licenses/$(PKGNAME)
PROJ      = subs2srs/subs2srs.csproj
PUBLISH   = subs2srs/bin/Release/net10.0/publish
TESTPROJ  = subs2srs.Tests/subs2srs.Tests.csproj

UITESTPROJ = subs2srs.UiTests/subs2srs.UiTests.csproj
EVALPROJ  = subs2srs.Eval/subs2srs.Eval.csproj
WINDIR    = out/win-x64
MSYS2     ?= C:/msys64
PWSH      ?= pwsh   # use PWSH=powershell on a machine without PowerShell 7

.PHONY: build test test-ui eval publish-windows install uninstall clean

build:
	dotnet publish $(PROJ) -c Release --no-self-contained

test:
	dotnet test $(TESTPROJ) -c Release

# GTK UI tests need a display; on a headless Linux box wrap with xvfb-run.
test-ui:
	GSK_RENDERER=cairo dotnet test $(UITESTPROJ) -c Release

# Grouping evaluation console (plan 9.3): make eval ARGS="--set validation/ --rules"
eval:
	dotnet run --project $(EVALPROJ) -c Release -- $(ARGS)

# Self-contained Windows build with the GTK runtime bundled from MSYS2 UCRT64.
# Run from PowerShell/Git Bash on Windows with mingw-w64-ucrt-x86_64-{gtk4,ntldd} installed.
publish-windows:
	dotnet publish $(PROJ) -c Release -r win-x64 --self-contained true -o $(WINDIR)
	$(PWSH) -NoProfile -File dist/windows/bundle-gtk.ps1 -Msys2Root "$(MSYS2)" -PublishDir "$(WINDIR)"
	$(PWSH) -NoProfile -File dist/windows/smoke.ps1 -PublishDir "$(WINDIR)"

install: build
	install -dm755 "$(LIBDIR)"
	cp -r $(PUBLISH)/* "$(LIBDIR)/"
	install -Dm755 dist/subs2srs.sh "$(BINDIR)/subs2srs"
	install -Dm644 dist/subs2srs.desktop "$(APPDIR)/subs2srs.desktop"
	install -Dm644 LICENSE "$(LICDIR)/LICENSE"

uninstall:
	rm -rf "$(DESTDIR)$(PREFIX)/lib/$(PKGNAME)"
	rm -f  "$(BINDIR)/subs2srs"
	rm -f  "$(APPDIR)/subs2srs.desktop"
	rm -rf "$(LICDIR)"

clean:
	dotnet clean $(PROJ) -c Release 2>/dev/null || true
	rm -rf subs2srs/bin subs2srs/obj
	rm -rf subs2srs.Tests/bin subs2srs.Tests/obj
	rm -rf subs2srs.UiTests/bin subs2srs.UiTests/obj
	rm -rf subs2srs.Eval/bin subs2srs.Eval/obj
	rm -rf out
