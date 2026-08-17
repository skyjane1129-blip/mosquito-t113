# Mosquito T113-S3 known-good baseline

- Saved: 2026-08-14 (Asia/Hong_Kong)
- Image: `mosquito-t113-boot-ok.img`
- Size: 8,796,160 bytes
- SHA256: `4b8dcb0da93cfe805e684e5997ee83f35bcdb928b0e847705763c4dd496fdff9`
- Original build: `tina_t113-mosquito_uart0.img`, 2026-08-13 23:25:14 +0800

## Verified on the custom PCB

- Boot0 and U-Boot boot from TF/SDC0.
- Linux 5.4.61 reaches the BusyBox/Tina root shell on UART0 at 115200 8N1.
- Both Cortex-A7 CPUs start successfully.
- The root filesystem mounts from the TF card.
- OP-TEE is not included in the boot package.
- SDC0 is treated as non-removable because the socket CD signal reports the wrong state on this PCB.

This image is a recovery point. Do not replace it with later experimental builds.
