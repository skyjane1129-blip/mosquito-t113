# Mosquito T113-S3 whole-board diagnostic image v2 (USB0 boot fix)

Built on 2026-08-14 for the `t113-mosquito` Tina Linux target.

Image: `mosquito-t113-board-test-v2-usb0-fix.img`

SHA256:

`054e0e049bfd0df3d47e98ececc6f5665dbbda84bcab1822689bd7cdd4c06e75`

## What v2 fixes

The first board-test image booted TF, UART1, TWI0 and USB1 successfully, but the
Allwinner 5.4 USB manager crashed while enabling fixed USB0 device mode:

```text
Unable to handle kernel NULL pointer dereference
PC is at typec_set_data_role
Comm: usb_device_chos
```

Mosquito has a passive Type-C device connector with fixed 5.1 kOhm Rd
resistors; it has no Type-C/PD controller. V2 therefore disables the unrelated
Linux `CONFIG_TYPEC` software class while retaining the Allwinner USB device
controller and CDC-ACM gadget support. The NULL call is no longer linked into
the kernel.

## Run

Connect UART0 at 115200, 8N1, no flow control. When the Tina shell appears:

```sh
board-test
```

For the fullest run, first insert a usable SIM, connect LTE/GNSS antennas,
insert a known-good USB flash drive into CN2, and connect TYPE_C1 to a PC.

The persistent report is:

```text
/overlay/mosquito-board-test.log
```

The test does not pulse Air780EG RESET_N and does not write BQ25895/EA3056
configuration registers. Voltage accuracy, RF antenna quality, buttons,
connector contact and Air780EG CN3 USB enumeration remain manual tests.

## Burning warning

This is a full-card image. Burning it overwrites the selected TF card's system
and partition table. Double-check the target disk and keep the known-good
baseline image available.
