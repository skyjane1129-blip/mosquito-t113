# Mosquito T113-S3 USB0 one-command test image v3

Built on 2026-08-14 for the `t113-mosquito` Tina Linux target.

Image: `mosquito-t113-usb0-test-v3.img`

SHA256:

`e0fe7ef540a9ddfcf7391848f396b9f2f765f56f8749d89ed0984a808165ab59`

## Purpose

This image keeps the existing whole-board diagnostic as `board-test` and adds
the simpler `usb0-test` command. The latter tests only T113 USB0 device mode;
it does not start Air780EG or run the other board tests.

`usb0-test` automatically:

1. Finds the Allwinner USB device controller.
2. Mounts configfs at `/tmp/mosquito-configfs`.
3. Creates a CDC-ACM USB serial gadget named `Mosquito Board Test`.
4. Binds it to `4100000.udc-controller`.
5. Waits up to five seconds for a connected computer to enumerate it.

## Run USB0 test

Keep UART0 connected to MobaXterm at 115200, 8N1, no flow control. UART0 is
still the debug console and is independent of USB0.

Connect the board's `TYPE_C1` connector to a computer with a known-good USB
data cable. At the Tina shell, run only:

```sh
usb0-test
```

A complete hardware-path success ends with output similar to:

```text
[PASS] USB0-UDC          device controller present: 4100000.udc-controller
[PASS] USB0-gadget       CDC-ACM gadget is bound as 'Mosquito USB0 Test'
[PASS] USB0-data         external host configured the gadget; Type-C data path works
PASS=3  FAIL=0  MANUAL=0
```

The computer should show a USB serial/composite device whose product string is
`Mosquito Board Test`. On Linux, `lsusb` can confirm it. On Windows, check
Device Manager. The board-side CDC serial node is `/dev/ttyGS0`.

If `USB0-data` reports `not attached`, the controller and gadget software are
working but the computer has not completed enumeration. Check that TYPE_C1 is
connected to a computer rather than a charger, try another data cable/USB port,
and rerun `usb0-test`.

The temporary USB0 report is `/tmp/mosquito-usb0-test.log`.

## Existing whole-board test

The full diagnostic remains available and unchanged in scope:

```sh
board-test
```

## Burning warning

This is a full-card image. Burning it overwrites the selected TF card's system
and partition table. Double-check the target disk and keep the known-good
baseline image available.
