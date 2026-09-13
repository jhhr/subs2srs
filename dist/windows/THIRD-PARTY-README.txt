The DLLs next to subs2srs.exe (libgtk-4-1.dll, libglib-2.0-0.dll, libpango-1.0-0.dll,
libcairo-2.dll, ...) and the files under share\ and lib\ are the GTK 4 runtime and its
dependencies, taken unmodified from the MSYS2 project's UCRT64 packages
(https://www.msys2.org/). Their licenses (LGPL 2.1+, MIT, BSD, ...) are reproduced in
this directory, one folder per package. gtk-bundle-manifest.txt lists the exact
package versions. Source code for each package is available from
https://packages.msys2.org/.

subs2srs itself is GPL-3.0-or-later; see LICENSE.
