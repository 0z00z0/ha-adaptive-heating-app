# Adaptive Heating — the add-on

Planning, learning and the pages. Each room's thermostat comes from the [Adaptive Heating integration](https://github.com/0z00z0/ha-adaptive-heating), which is installed
separately from the Marketplace and runs on its own.

## What it serves

A dashboard, an activity page, a page per room and the settings, reached from **Heating** in Home Assistant's
sidebar. Home Assistant's own sign-in guards them, and only administrator accounts see the entry. The manifest maps
no port, so the add-on has no address of its own on the box.

## Installing

Add `https://github.com/0z00z0/ha-adaptive-heating-app` under **Settings → Apps → Install app → ⋮ → Repositories**,
then install **Adaptive Heating**. The manifest names no image, so the Supervisor builds the add-on on the box and
the install is not instant. The integration's README walks every step.

Supported boards are `aarch64` and `amd64`. A 32-bit board is not covered.

## Installing by hand

1. Copy this folder, `adaptive_heating/`, with everything in it, to `/addons/adaptive_heating/` on the box.
2. Reload the store. **Adaptive Heating** appears in the section for local apps.
3. Install it, and carry on from step 4 of the integration's README.

To update, replace the folder's contents and rebuild the add-on from its own page.

## Building the image off the box

This folder is the build context, as it is for the Supervisor. The add-on's source sits in `addon/` inside it.

`docker buildx build --platform linux/arm64,linux/amd64 -t <image>:<version> adaptive_heating`

The publish carries no runtime identifier, so one compile serves every board and only the runtime base differs.

## What survives an update

`/data` is the add-on's own persistent directory, and an update does not replace it. It holds:

- the settings document, `/data/AdaptiveHeating.yaml`, and its one backup beside it;
- the notes in `/data/state/`: each room's learnt numbers and stamps, the activity record, the room history and the
  warm-ups awaiting the record calendar;
- the last-seen cache, the durable log and the command log;
- the page's key ring in `/data/keys`, so an update does not sign every open page out.
