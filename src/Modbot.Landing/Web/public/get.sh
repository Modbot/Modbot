#!/bin/sh
# Modbot — set it up on this machine.
#
#   curl -fsSL https://modbot.co/get.sh | sh
#
# It installs Docker if it is missing, writes a docker-compose.yml and a .env in a folder you
# choose, makes a modbot-data folder beside them for logs, evidence and crash dumps, and starts
# Modbot and its database in the background.
#
# This is served as plain text so you can read it before you run it.
#
# It asks a few questions. A script piped to sh gets the script itself on standard input, so the
# answers are read from the terminal directly. Where there is no terminal — a CI job, a cron
# line — nothing is asked and these are used instead:
#
#   MODBOT_DIR              Folder to set Modbot up in. The folder you are in now.
#   MODBOT_BUILD            release (the default) or preview.
#   MODBOT_PORT             The port to open Modbot on. 8080.
#   POSTGRES_PASSWORD       The database password. A long random one is made when this is unset.
#   MODBOT_INSTALL_DOCKER   yes to install Docker without asking, no to never install it. yes.
#   MODBOT_SITE             Where get.sh and docker-compose.yml come from. https://modbot.co.
#
# Everything else about Modbot — the VRChat account, Discord, email, AI — is set up in the browser
# afterwards. https://docs.modbot.co/self-hosting

set -eu

# Held in a function so a half-downloaded copy of this file runs nothing: sh reads the whole
# definition before the last line calls it.
main() {
	SITE=${MODBOT_SITE:-https://modbot.co}
	RELEASE_IMAGE=ghcr.io/modbot/modbot:latest
	PREVIEW_IMAGE=ghcr.io/modbot/modbot:latest-preview

	TTY=no
	if (exec 3</dev/tty) 2>/dev/null; then
		TTY=yes
	fi

	SUDO=''
	if [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1; then
		SUDO=sudo
	fi

	say ''
	say 'Modbot'
	say '------'
	say ''

	choose_folder
	have_docker
	have_compose
	old_settings
	choose_build
	write_files
	pull_image
	start_modbot
	finish
}

# ── Saying things ────────────────────────────────────────────────────────────

say() {
	printf '%s\n' "$*"
}

die() {
	printf '%s\n' "$*" >&2
	exit 1
}

# ── Asking things ────────────────────────────────────────────────────────────
#
# The prompt goes to the terminal rather than to standard output, because these are called inside
# $( ), which would swallow it.

ask() {
	ask_default=$2

	if [ "$TTY" = no ]; then
		printf '%s\n' "$ask_default"
		return 0
	fi

	printf '%s' "$1" >/dev/tty
	ask_reply=''
	IFS= read -r ask_reply </dev/tty || ask_reply=''

	if [ -z "$ask_reply" ]; then
		ask_reply=$ask_default
	fi

	printf '%s\n' "$ask_reply"
}

confirm() {
	if [ "$2" = y ]; then
		confirm_hint='[Y/n] '
	else
		confirm_hint='[y/N] '
	fi

	confirm_answer=$(ask "$1 $confirm_hint" "$2")

	case $confirm_answer in
		y | Y | yes | Yes | YES) return 0 ;;
		*) return 1 ;;
	esac
}

# ── Getting files ────────────────────────────────────────────────────────────

download() {
	if command -v curl >/dev/null 2>&1; then
		curl -fsSL "$1" -o "$2"
	elif command -v wget >/dev/null 2>&1; then
		wget -qO "$2" "$1"
	else
		die "Downloading $1 needs curl or wget, and neither is installed."
	fi
}

# The value of one setting in a .env file, or nothing.
env_value() {
	sed -n "s/^$1=//p" "$2" 2>/dev/null | tail -n 1
}

# ── Where it goes ────────────────────────────────────────────────────────────

choose_folder() {
	here=${MODBOT_DIR:-$(pwd)}
	DIR=$(ask "Folder to set Modbot up in [$here]: " "$here")

	mkdir -p "$DIR"
	cd "$DIR"
	DIR=$(pwd)

	say "Setting Modbot up in $DIR"
	say ''
}

# ── Docker ───────────────────────────────────────────────────────────────────

have_docker() {
	if command -v docker >/dev/null 2>&1; then
		say 'Docker is already installed.'
	else
		install_docker
	fi

	# Installed but unreachable means the service is not running, or this account is not in the
	# docker group. Root can usually reach it either way, so try that before giving up.
	DOCKER=docker

	if ! docker info >/dev/null 2>&1; then
		if [ -n "$SUDO" ] && $SUDO docker info >/dev/null 2>&1; then
			DOCKER="$SUDO docker"
			say 'Docker needs root on this machine, so the commands below use sudo.'
		else
			die 'Docker is installed but not answering. Start it (sudo systemctl start docker),
or add yourself to the docker group and sign in again, then run this script again.'
		fi
	fi
}

install_docker() {
	say 'Docker is not installed.'

	if [ "${MODBOT_INSTALL_DOCKER:-yes}" = no ]; then
		die 'MODBOT_INSTALL_DOCKER is no, so Docker was not installed. Install it yourself
and run this script again: https://docs.docker.com/engine/install/'
	fi

	if [ "$(id -u)" -ne 0 ]; then
		if [ -z "$SUDO" ]; then
			die 'Installing Docker needs root, and sudo is not installed. Install Docker
yourself and run this script again: https://docs.docker.com/engine/install/'
		fi

		say 'Installing it needs root. The install runs through sudo, which will ask for your password.'
	fi

	if ! confirm 'Install Docker now?' y; then
		die 'Nothing was installed. Install Docker yourself and run this script again:
https://docs.docker.com/engine/install/'
	fi

	# Docker's own install script, so this one never carries a package list per distribution. The
	# name is Modbot's so that a get-docker.sh somebody already had here is not written over.
	install_script=./modbot-get-docker.sh
	download https://get.docker.com "$install_script"
	$SUDO sh "$install_script"
	rm -f "$install_script"

	command -v docker >/dev/null 2>&1 ||
		die 'Docker still is not installed. See https://docs.docker.com/engine/install/'

	say 'Docker is installed.'
}

have_compose() {
	if $DOCKER compose version >/dev/null 2>&1; then
		return 0
	fi

	if command -v docker-compose >/dev/null 2>&1; then
		die 'This machine has the old docker-compose command, not the Compose plugin Modbot
needs. Install the plugin and run this script again:
https://docs.docker.com/compose/install/linux/'
	fi

	die 'Docker Compose is not installed. Install it and run this script again:
https://docs.docker.com/compose/install/'
}

# ── A Modbot that is already set up here ─────────────────────────────────────

old_settings() {
	KEEP_SETTINGS=no

	[ -f .env ] || return 0

	say 'There is already a .env here, holding the settings of a Modbot set up earlier.'
	say 'It has the password the database on this machine was made with.'

	# Keeping it is the default, and the only answer a run with no terminal gives: a fresh password
	# would leave Modbot unable to sign in to the database that is already there.
	if confirm 'Keep those settings?' y; then
		KEEP_SETTINGS=yes
		IMAGE=$(env_value MODBOT_IMAGE .env)
		IMAGE=${IMAGE:-$RELEASE_IMAGE}
		PORT=$(env_value MODBOT_PORT .env)
		PORT=${PORT:-8080}
		say "Keeping them. Modbot stays on $IMAGE"
		say ''
		return 0
	fi

	mv .env .env.old
	say 'The old one is now .env.old'
	say ''
}

# ── Which build ──────────────────────────────────────────────────────────────

choose_build() {
	[ "$KEEP_SETTINGS" = no ] || return 0

	build=$(ask 'Which build — release or preview? [release]: ' "${MODBOT_BUILD:-release}")

	case $build in
		release | Release | RELEASE | latest)
			IMAGE=$RELEASE_IMAGE
			;;
		preview | Preview | PREVIEW | latest-preview)
			IMAGE=$PREVIEW_IMAGE
			;;
		*)
			die "\"$build\" is not a build. Answer release or preview."
			;;
	esac

	say "Using $IMAGE"
	say ''
}

# ── The password and the port ────────────────────────────────────────────────

random_password() {
	if [ -r /dev/urandom ]; then
		od -An -N 24 -t x1 /dev/urandom | tr -d ' \n'
		printf '\n'
	elif command -v openssl >/dev/null 2>&1; then
		openssl rand -hex 24
	else
		printf '\n'
	fi
}

check_password() {
	# The database address is built as a keyword connection string, and Compose reads the value
	# back out of .env, so none of these can be carried through safely.
	case $1 in
		*';'* | *'$'* | *'"'* | *"'"* | *'\'*)
			die 'The database password cannot contain ; $ " '"'"' or \'
			;;
	esac
}

check_port() {
	case $1 in
		'' | *[!0-9]*) die "\"$1\" is not a port number." ;;
	esac

	if [ "$1" -lt 1 ] || [ "$1" -gt 65535 ]; then
		die "\"$1\" is not a port number."
	fi
}

# ── Writing the files ────────────────────────────────────────────────────────

write_files() {
	mkdir -p modbot-data/logs modbot-data/evidence modbot-data/dumps

	write_settings
	write_compose
}

write_settings() {
	[ "$KEEP_SETTINGS" = no ] || return 0

	password=$(ask 'Database password [a random one]: ' "${POSTGRES_PASSWORD:-$(random_password)}")
	[ -n "$password" ] ||
		die 'No password could be made on this machine. Set POSTGRES_PASSWORD and run this script again.'
	check_password "$password"

	PORT=$(ask 'Port to open Modbot on [8080]: ' "${MODBOT_PORT:-8080}")
	check_port "$PORT"

	# The file holds the database password, so only its owner may read it.
	(
		umask 077
		{
			printf '%s\n' '# Modbot settings. Written by https://modbot.co/get.sh'
			printf '%s\n' '# Everything else is set up in the browser.'
			printf '%s\n' ''
			printf 'MODBOT_IMAGE=%s\n' "$IMAGE"
			printf 'MODBOT_PORT=%s\n' "$PORT"
			printf 'POSTGRES_PASSWORD=%s\n' "$password"
			printf 'POSTGRES_DB=%s\n' modbot
			printf 'POSTGRES_USER=%s\n' modbot
		} >.env
	)
	chmod 600 .env 2>/dev/null || true

	say 'Wrote .env'
	say ''
}

write_compose() {
	download "$SITE/docker-compose.yml" ./docker-compose.yml.new

	if [ -f docker-compose.yml ]; then
		if cmp -s docker-compose.yml docker-compose.yml.new; then
			rm -f docker-compose.yml.new
			return 0
		fi

		say 'The docker-compose.yml here is not the one modbot.co serves now.'

		if ! confirm 'Replace it?' y; then
			rm -f docker-compose.yml.new
			say 'Keeping the one that was already here.'
			say ''
			return 0
		fi

		mv docker-compose.yml docker-compose.yml.old
		say 'The old one is now docker-compose.yml.old'
	fi

	mv docker-compose.yml.new docker-compose.yml
	say 'Wrote docker-compose.yml'
	say ''
}

# ── Getting the image ────────────────────────────────────────────────────────

pull_image() {
	image=$(env_value MODBOT_IMAGE .env)
	image=${image:-$IMAGE}

	say "Pulling $image"

	pull_log=./.modbot-pull.log

	if $DOCKER pull "$image" >"$pull_log" 2>&1; then
		rm -f "$pull_log"
		return 0
	fi

	trouble=$(cat "$pull_log")
	rm -f "$pull_log"

	say ''
	say "Modbot's image could not be pulled. Docker said:"
	say ''
	say "$trouble"
	say ''

	case $trouble in
		*'authentication required'* | *denied* | *unauthorized* | *forbidden*)
			die "$image is not open to everyone yet. Sign in to GitHub's registry with a
token that has the read:packages scope, then run this script again:

  echo \"\$GITHUB_TOKEN\" | docker login ghcr.io -u your-github-username --password-stdin"
			;;
		*'manifest unknown'* | *'not found'* | *'manifest for'*)
			die "There is no $image to pull. The tags there are on
https://docs.modbot.co/self-hosting/docker"
			;;
		*)
			die 'Nothing was started. Fix what Docker reported above and run this script again.'
			;;
	esac
}

# ── Starting it ──────────────────────────────────────────────────────────────

start_modbot() {
	say 'Starting Modbot'

	if ! $DOCKER compose up -d; then
		die 'Modbot did not start. Docker gave the reason above. The files here are written and
correct, so fix that and run "docker compose up -d" in this folder.'
	fi
}

finish() {
	address="http://localhost:${PORT:-8080}"

	say ''
	say "Modbot is at $address"
	say "Its files are in $DIR/modbot-data"
	say ''
	say "To stop it:    cd $DIR && ${DOCKER} compose down"
	say "To update it:  cd $DIR && ${DOCKER} compose pull && ${DOCKER} compose up -d"
	say ''
}

main "$@"
