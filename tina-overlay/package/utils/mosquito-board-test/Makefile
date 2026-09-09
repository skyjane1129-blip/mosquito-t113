include $(TOPDIR)/rules.mk

PKG_NAME:=mosquito-board-test
PKG_VERSION:=2.18
PKG_RELEASE:=1
PKG_BUILD_DIR:=$(COMPILE_DIR)/$(PKG_NAME)

include $(BUILD_DIR)/package.mk

define Package/mosquito-board-test
  SECTION:=utils
  CATEGORY:=Utilities
  TITLE:=Mosquito T113-S3 whole-board diagnostic
  DEPENDS:=+adb +usbutils +v4l-utils +ppp +chat +curl +ca-certificates
endef

define Package/mosquito-board-test/description
 One-command, non-destructive board diagnostic for the Mosquito T113-S3 PCB.
endef

define Build/Prepare
	mkdir -p $(PKG_BUILD_DIR)
	$(CP) ./src/* $(PKG_BUILD_DIR)/
endef

define Build/Configure
endef

define Build/Compile
	$(MAKE) -C $(PKG_BUILD_DIR) \
		CC="$(TARGET_CC)" \
		CFLAGS="$(TARGET_CFLAGS) -std=gnu99 -Wall -Wextra" \
		LDFLAGS="$(TARGET_LDFLAGS)"
endef

define Package/mosquito-board-test/install
	$(INSTALL_DIR) $(1)/usr/bin
	$(INSTALL_BIN) $(PKG_BUILD_DIR)/mosquito-board-test $(1)/usr/bin/
	$(LN) mosquito-board-test $(1)/usr/bin/board-test
	$(LN) mosquito-board-test $(1)/usr/bin/air-test
	$(LN) mosquito-board-test $(1)/usr/bin/gnss-test
	$(LN) mosquito-board-test $(1)/usr/bin/sim-test
	$(LN) mosquito-board-test $(1)/usr/bin/power-test
	$(LN) mosquito-board-test $(1)/usr/bin/mosquito-environment
	$(LN) mosquito-board-test $(1)/usr/bin/mosquito-power
	$(INSTALL_BIN) ./files/usb0-status $(1)/usr/bin/
	$(LN) usb0-status $(1)/usr/bin/usb0-test
	$(INSTALL_BIN) ./files/usb0-adb-start $(1)/usr/bin/
	$(LN) usb0-adb-start $(1)/usr/bin/adb-on
	$(INSTALL_BIN) ./files/usb0-adb-stop $(1)/usr/bin/
	$(INSTALL_BIN) ./files/camera-test $(1)/usr/bin/
	$(INSTALL_BIN) ./files/camera-test1 $(1)/usr/bin/
	$(INSTALL_BIN) ./files/mosquito-capture $(1)/usr/bin/
	$(INSTALL_BIN) ./files/camera-test0 $(1)/usr/bin/
	$(INSTALL_BIN) ./files/mosquito-version $(1)/usr/bin/
	$(INSTALL_BIN) ./files/4g-start $(1)/usr/bin/
	$(INSTALL_BIN) ./files/4g-stop $(1)/usr/bin/
	$(INSTALL_BIN) ./files/photo-upload $(1)/usr/bin/
	$(INSTALL_DIR) $(1)/etc/ppp/peers
	$(INSTALL_CONF) ./files/air780eg.peer $(1)/etc/ppp/peers/air780eg
	$(INSTALL_DIR) $(1)/etc/mosquito
	$(INSTALL_CONF) ./files/4g.conf $(1)/etc/mosquito/4g.conf
	$(INSTALL_DATA) ./files/mosquito-version.conf $(1)/etc/mosquito-version
	$(LN) /tmp/resolv.conf.auto $(1)/etc/resolv.conf
	$(INSTALL_DIR) $(1)/etc/ssl/certs
	$(INSTALL_DATA) ./files/ISRG_Root_X1.crt $(1)/etc/ssl/certs/ISRG_Root_X1.crt
	$(INSTALL_DIR) $(1)/etc/init.d
	# Replace Tina's generic service with the Mosquito FunctionFS adbd service.
	$(INSTALL_BIN) ./files/mosquito-adbd.init $(1)/etc/init.d/adbd
endef

$(eval $(call BuildPackage,mosquito-board-test))
