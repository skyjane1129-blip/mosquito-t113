include $(TOPDIR)/rules.mk

PKG_NAME:=mosquito-board-test
PKG_VERSION:=2.3
PKG_RELEASE:=1
PKG_BUILD_DIR:=$(COMPILE_DIR)/$(PKG_NAME)

include $(BUILD_DIR)/package.mk

define Package/mosquito-board-test
  SECTION:=utils
  CATEGORY:=Utilities
  TITLE:=Mosquito T113-S3 whole-board diagnostic
  DEPENDS:=+adb +usbutils +v4l-utils
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
	$(INSTALL_BIN) ./files/usb0-status $(1)/usr/bin/
	$(LN) usb0-status $(1)/usr/bin/usb0-test
	$(INSTALL_BIN) ./files/usb0-adb-start $(1)/usr/bin/
	$(INSTALL_BIN) ./files/usb0-adb-stop $(1)/usr/bin/
	$(INSTALL_BIN) ./files/camera-test $(1)/usr/bin/
	$(INSTALL_DIR) $(1)/etc/init.d
	# Replace Tina's generic auto-starting service with a manual-only service.
	# usb0-adb-start prepares FunctionFS and starts this service after boot.
	$(INSTALL_BIN) ./files/mosquito-adbd.init $(1)/etc/init.d/adbd
endef

$(eval $(call BuildPackage,mosquito-board-test))
