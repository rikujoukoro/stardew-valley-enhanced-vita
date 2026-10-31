#!/usr/bin/env bash
# Installs VitaSDK (toolchain + all vdpm packages) into /usr/local/vitasdk, inside WSL Ubuntu.
# Run as root:  wsl -d Ubuntu -u root -- bash /mnt/c/.../Tools/Vita/install-vitasdk.sh
set -euo pipefail

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"
WORK=/root/vita
mkdir -p "$WORK"
cd "$WORK"

if [ ! -d vdpm ]; then
	git clone --depth 1 https://github.com/vitasdk/vdpm
fi
cd vdpm

if [ ! -x "$VITASDK/bin/arm-vita-eabi-gcc" ]; then
	./bootstrap-vitasdk.sh
fi
# install-all.sh calls the repository's legacy ./vdpm shell script, which expects pacman in
# $VITASDK/bin; the vdpm binary installed by the bootstrap uses libexec/vdpm/pacman. Install the
# same package list with the installed vdpm instead.
PACKAGES=$(sed -n '/^PACKAGES=(/,/^)/p' include/install-packages.sh | sed '1d;$d' | tr -d '\t' | tr '\n' ' ')
# One at a time: the list still names packages the channel no longer has (e.g. cpython), and pacman
# aborts the whole transaction on one unknown target.
MISSING=""
for p in $PACKAGES; do
	# Not 'yes | vdpm': with pipefail, yes dying of SIGPIPE would mark every install as failed.
	if ! "$VITASDK/bin/vdpm" install "$p" < <(yes) > /tmp/vdpm-$p.log 2>&1; then
		MISSING="$MISSING $p"
	fi
done
echo "packages not installed:${MISSING:- none}"

# Make the environment available to later shells.
if ! grep -q "VITASDK=" /root/.bashrc; then
	echo 'export VITASDK=/usr/local/vitasdk' >> /root/.bashrc
	echo 'export PATH="$VITASDK/bin:$PATH"' >> /root/.bashrc
fi

arm-vita-eabi-gcc --version | head -1
ls "$VITASDK/share/vita.toolchain.cmake"
