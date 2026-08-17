#define _GNU_SOURCE

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <linux/i2c-dev.h>
#include <linux/i2c.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/file.h>
#include <sys/select.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

#define ARRAY_SIZE(a) (sizeof(a) / sizeof((a)[0]))
#define GPIO_PD21 (3 * 32 + 21)
#define GPIO_PE0  (4 * 32 + 0)
#define GPIO_PE1  (4 * 32 + 1)
#define GPIO_PE5  (4 * 32 + 5)
#define GPIO_PE7  (4 * 32 + 7)
#define GPIO_PE12 (4 * 32 + 12)
#define GPIO_PE13 (4 * 32 + 13)
#define GPIO_PF6  (5 * 32 + 6)

enum result_kind { R_PASS, R_FAIL, R_WARN, R_MANUAL, R_INFO };

static FILE *g_log;
static int g_pass;
static int g_fail;
static int g_warn;
static int g_manual;
static int g_gpio_base = -1;
static volatile sig_atomic_t g_gnss_stop;

static void request_gnss_stop(int signo)
{
	(void)signo;
	g_gnss_stop = 1;
}

static void emit(const char *fmt, ...)
{
	char line[2048];
	va_list ap;

	va_start(ap, fmt);
	vsnprintf(line, sizeof(line), fmt, ap);
	va_end(ap);
	fputs(line, stdout);
	fflush(stdout);
	if (g_log) {
		fputs(line, g_log);
		fflush(g_log);
	}
}

static void result(enum result_kind kind, const char *name, const char *fmt, ...)
{
	const char *tag;
	char detail[1536];
	va_list ap;

	switch (kind) {
	case R_PASS: tag = "PASS"; ++g_pass; break;
	case R_FAIL: tag = "FAIL"; ++g_fail; break;
	case R_WARN: tag = "WARN"; ++g_warn; break;
	case R_MANUAL: tag = "MANUAL"; ++g_manual; break;
	default: tag = "INFO"; break;
	}
	va_start(ap, fmt);
	vsnprintf(detail, sizeof(detail), fmt, ap);
	va_end(ap);
	emit("[%s] %-18s %s\n", tag, name, detail);
}

static int read_file(const char *path, char *buf, size_t size)
{
	int fd;
	ssize_t n;

	if (!size)
		return -1;
	fd = open(path, O_RDONLY);
	if (fd < 0)
		return -1;
	n = read(fd, buf, size - 1);
	close(fd);
	if (n < 0)
		return -1;
	buf[n] = '\0';
	while (n > 0 && (buf[n - 1] == '\n' || buf[n - 1] == '\r' || buf[n - 1] == '\0'))
		buf[--n] = '\0';
	return (int)n;
}

static int write_file(const char *path, const char *value)
{
	int fd;
	ssize_t len = (ssize_t)strlen(value);
	ssize_t n;

	fd = open(path, O_WRONLY | O_TRUNC);
	if (fd < 0)
		return -1;
	n = write(fd, value, (size_t)len);
	close(fd);
	return n == len ? 0 : -1;
}

static int copy_file(const char *from, const char *to)
{
	char buf[4096];
	int in = -1, out = -1;
	ssize_t n;

	in = open(from, O_RDONLY);
	if (in < 0)
		return -1;
	out = open(to, O_WRONLY | O_CREAT | O_TRUNC, 0644);
	if (out < 0) {
		close(in);
		return -1;
	}
	while ((n = read(in, buf, sizeof(buf))) > 0) {
		if (write(out, buf, (size_t)n) != n) {
			close(in);
			close(out);
			return -1;
		}
	}
	fsync(out);
	close(in);
	close(out);
	return n < 0 ? -1 : 0;
}

static long long monotonic_ms(void)
{
	struct timespec ts;
	clock_gettime(CLOCK_MONOTONIC, &ts);
	return (long long)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static void section(const char *name)
{
	emit("\n========== %s ==========\n", name);
}

static void test_board_basics(void)
{
	char buf[512] = { 0 };
	char sizebuf[64];
	unsigned long long sectors = 0;
	int fd;
	unsigned char block[512];
	DIR *dir;
	struct dirent *de;
	int partitions = 0;
	char temp_path[128];

	section("1. T113 / MEMORY / TF CARD");
	if (read_file("/proc/device-tree/model", buf, sizeof(buf)) > 0 && strstr(buf, "Mosquito"))
		result(R_PASS, "board-model", "%s", buf);
	else
		result(R_FAIL, "board-model", "unexpected model: %s", buf[0] ? buf : "unreadable");

	if (read_file("/sys/devices/system/cpu/online", buf, sizeof(buf)) > 0)
		result(strstr(buf, "0-1") ? R_PASS : R_WARN, "cpu", "online CPUs: %s", buf);
	else
		result(R_WARN, "cpu", "cannot read CPU online state");

	if (read_file("/proc/meminfo", buf, sizeof(buf)) > 0)
		result(R_INFO, "memory", "%.80s", buf);

	fd = open("/dev/mmcblk0", O_RDONLY);
	if (fd >= 0 && pread(fd, block, sizeof(block), 0) == (ssize_t)sizeof(block)) {
		result(R_PASS, "tf-card", "raw read from /dev/mmcblk0 succeeded");
		close(fd);
	} else {
		if (fd >= 0)
			close(fd);
		result(R_FAIL, "tf-card", "cannot read /dev/mmcblk0: %s", strerror(errno));
	}
	if (read_file("/sys/block/mmcblk0/size", sizebuf, sizeof(sizebuf)) > 0) {
		sectors = strtoull(sizebuf, NULL, 10);
		result(R_INFO, "tf-capacity", "%.1f GiB", sectors / 2097152.0);
	}
	dir = opendir("/sys/block/mmcblk0");
	if (dir) {
		while ((de = readdir(dir)) != NULL)
			if (!strncmp(de->d_name, "mmcblk0p", 8))
				++partitions;
		closedir(dir);
	}
	result(partitions >= 7 ? R_PASS : R_WARN, "tf-partitions", "found %d partitions (image expects 7)", partitions);

	snprintf(temp_path, sizeof(temp_path), "/overlay/.mosquito-write-test-%ld", (long)getpid());
	fd = open(temp_path, O_WRONLY | O_CREAT | O_EXCL, 0600);
	if (fd >= 0 && write(fd, "ok\n", 3) == 3 && fsync(fd) == 0) {
		close(fd);
		unlink(temp_path);
		result(R_PASS, "tf-write", "rootfs_data overlay write/read path is writable");
	} else {
		if (fd >= 0) close(fd);
		unlink(temp_path);
		result(R_WARN, "tf-write", "cannot verify /overlay write: %s", strerror(errno));
	}
}

static int find_gpio_base(void)
{
	DIR *dir;
	struct dirent *de;
	int selected = -1;
	long selected_ngpio = -1;
	char path[PATH_MAX], value[128];

	dir = opendir("/sys/class/gpio");
	if (!dir)
		return -1;
	while ((de = readdir(dir)) != NULL) {
		long base, ngpio;
		if (strncmp(de->d_name, "gpiochip", 8))
			continue;
		snprintf(path, sizeof(path), "/sys/class/gpio/%s/base", de->d_name);
		if (read_file(path, value, sizeof(value)) < 0)
			continue;
		base = strtol(value, NULL, 10);
		snprintf(path, sizeof(path), "/sys/class/gpio/%s/ngpio", de->d_name);
		if (read_file(path, value, sizeof(value)) < 0)
			continue;
		ngpio = strtol(value, NULL, 10);
		if (ngpio > selected_ngpio) {
			selected = (int)base;
			selected_ngpio = ngpio;
		}
	}
	closedir(dir);
	return selected;
}

static int gpio_export(unsigned offset)
{
	char num[32], path[PATH_MAX];
	int fd;
	ssize_t n;
	int i;

	if (g_gpio_base < 0)
		return -1;
	snprintf(num, sizeof(num), "%d", g_gpio_base + (int)offset);
	fd = open("/sys/class/gpio/export", O_WRONLY);
	if (fd >= 0) {
		n = write(fd, num, strlen(num));
		if (n < 0 && errno != EBUSY) {
			close(fd);
			return -1;
		}
		close(fd);
	}
	snprintf(path, sizeof(path), "/sys/class/gpio/gpio%s/direction", num);
	for (i = 0; i < 50; ++i) {
		if (access(path, F_OK) == 0)
			return 0;
		usleep(10000);
	}
	return -1;
}

/* output_value: -1 input, 0 output-low, 1 output-high */
static int gpio_direction(unsigned offset, int output_value)
{
	char path[PATH_MAX];
	if (gpio_export(offset) < 0)
		return -1;
	snprintf(path, sizeof(path), "/sys/class/gpio/gpio%d/direction", g_gpio_base + (int)offset);
	return write_file(path, output_value < 0 ? "in" : (output_value ? "high" : "low"));
}

static int gpio_read_value(unsigned offset)
{
	char path[PATH_MAX], value[16];
	if (gpio_direction(offset, -1) < 0)
		return -1;
	snprintf(path, sizeof(path), "/sys/class/gpio/gpio%d/value", g_gpio_base + (int)offset);
	if (read_file(path, value, sizeof(value)) < 0)
		return -1;
	return atoi(value);
}

static void test_status_gpios(void)
{
	int v;
	section("2. GPIO STATUS INPUTS");
	g_gpio_base = find_gpio_base();
	if (g_gpio_base < 0) {
		result(R_FAIL, "gpio-controller", "cannot locate the main PIO gpiochip");
		return;
	}
	result(R_PASS, "gpio-controller", "main PIO base is %d", g_gpio_base);
	v = gpio_read_value(GPIO_PE13);
	result(v < 0 ? R_FAIL : R_INFO, "BQ25895-INT", v < 0 ? "cannot read PE13" : "PE13 raw level=%d (active low)", v);
	v = gpio_read_value(GPIO_PE12);
	result(v < 0 ? R_FAIL : R_INFO, "BQ25895-STAT", v < 0 ? "cannot read PE12" : "PE12 raw level=%d (active low/open drain)", v);
	v = gpio_read_value(GPIO_PE7);
	result(v < 0 ? R_FAIL : R_INFO, "Air-RI", v < 0 ? "cannot read PE7" : "PE7 raw level=%d", v);
	v = gpio_read_value(GPIO_PF6);
	result(v < 0 ? R_WARN : R_INFO, "TF-detect", v < 0 ? "cannot read PF6" : "PF6 raw level=%d; boot does not depend on this mechanical contact", v);
}

static int i2c_xfer(int fd, uint8_t address, const uint8_t *wbuf, uint16_t wlen,
		uint8_t *rbuf, uint16_t rlen)
{
	struct i2c_msg msgs[2];
	struct i2c_rdwr_ioctl_data rdwr;
	unsigned count = 0;

	memset(msgs, 0, sizeof(msgs));
	if (wlen) {
		msgs[count].addr = address;
		msgs[count].buf = (uint8_t *)wbuf;
		msgs[count].len = wlen;
		++count;
	}
	if (rlen) {
		msgs[count].addr = address;
		msgs[count].flags = I2C_M_RD;
		msgs[count].buf = rbuf;
		msgs[count].len = rlen;
		++count;
	}
	rdwr.msgs = msgs;
	rdwr.nmsgs = count;
	return ioctl(fd, I2C_RDWR, &rdwr) == (int)count ? 0 : -1;
}

static int i2c_read_reg(int fd, uint8_t address, uint8_t reg, uint8_t *value)
{
	return i2c_xfer(fd, address, &reg, 1, value, 1);
}

static uint8_t dht_crc8(const uint8_t *data, size_t len)
{
	uint8_t crc = 0xff;
	size_t i;
	int bit;
	for (i = 0; i < len; ++i) {
		crc ^= data[i];
		for (bit = 0; bit < 8; ++bit)
			crc = (crc & 0x80) ? (uint8_t)((crc << 1) ^ 0x31) : (uint8_t)(crc << 1);
	}
	return crc;
}

static void test_dht30(int fd)
{
	uint8_t status_cmd = 0x71;
	uint8_t init_cmd[3] = { 0xbe, 0x08, 0x00 };
	uint8_t measure_cmd[3] = { 0xac, 0x33, 0x00 };
	uint8_t status = 0;
	uint8_t data[7];
	uint32_t raw_h, raw_t;
	int rh100, temp100;
	int i;

	if (i2c_xfer(fd, 0x38, &status_cmd, 1, NULL, 0) < 0) {
		result(R_FAIL, "DHT30@0x38", "no acknowledgement on TWI0");
		return;
	}
	usleep(10000);
	if (i2c_xfer(fd, 0x38, NULL, 0, &status, 1) == 0 && !(status & 0x08)) {
		/* Volatile sensor calibration command; it does not alter PCB settings. */
		i2c_xfer(fd, 0x38, init_cmd, sizeof(init_cmd), NULL, 0);
		usleep(10000);
	}
	if (i2c_xfer(fd, 0x38, measure_cmd, sizeof(measure_cmd), NULL, 0) < 0) {
		result(R_FAIL, "DHT30@0x38", "address responds but measurement command failed");
		return;
	}
	for (i = 0; i < 10; ++i) {
		usleep(20000);
		if (i2c_xfer(fd, 0x38, NULL, 0, data, sizeof(data)) == 0 && !(data[0] & 0x80))
			break;
	}
	if (i == 10) {
		result(R_FAIL, "DHT30@0x38", "sensor stayed busy or returned no sample");
		return;
	}
	if (dht_crc8(data, 6) != data[6]) {
		result(R_FAIL, "DHT30@0x38", "sample CRC mismatch (got %02x, expected %02x)", data[6], dht_crc8(data, 6));
		return;
	}
	raw_h = ((uint32_t)data[1] << 12) | ((uint32_t)data[2] << 4) | (data[3] >> 4);
	raw_t = ((uint32_t)(data[3] & 0x0f) << 16) | ((uint32_t)data[4] << 8) | data[5];
	rh100 = (int)(((uint64_t)raw_h * 10000 + 524288) / 1048576);
	temp100 = (int)(((uint64_t)raw_t * 20000 + 524288) / 1048576) - 5000;
	if (rh100 >= 0 && rh100 <= 10000 && temp100 >= -4000 && temp100 <= 8500)
		result(R_PASS, "DHT30@0x38", "temperature=%s%d.%02d C, humidity=%d.%02d %%RH, CRC OK",
		       temp100 < 0 ? "-" : "", abs(temp100) / 100, abs(temp100) % 100, rh100 / 100, rh100 % 100);
	else
		result(R_FAIL, "DHT30@0x38", "out-of-range sample: T=%d.%02d C RH=%d.%02d%%",
		       temp100 / 100, abs(temp100) % 100, rh100 / 100, rh100 % 100);
}

static void test_bq25895(int fd)
{
	uint8_t r0b, r0c, r0e, r0f, r11, r14;
	int ok;
	static const char *const vbus_names[] = {
		"none", "USB-SDP", "USB-CDP", "USB-DCP", "MAXCHARGE", "unknown", "non-standard", "OTG"
	};
	static const char *const charge_names[] = { "not charging", "pre-charge", "fast-charge", "charge done" };

	ok = i2c_read_reg(fd, 0x6a, 0x14, &r14);
	if (ok < 0) {
		result(R_FAIL, "BQ25895@0x6a", "cannot read the part-information register");
		return;
	}
	result(R_PASS, "BQ25895@0x6a", "part register 0x14=0x%02x (PN=%u, revision=%u)",
	       r14, (r14 >> 3) & 0x07, r14 & 0x03);
	if (i2c_read_reg(fd, 0x6a, 0x0b, &r0b) == 0 &&
	    i2c_read_reg(fd, 0x6a, 0x0c, &r0c) == 0 &&
	    i2c_read_reg(fd, 0x6a, 0x0e, &r0e) == 0 &&
	    i2c_read_reg(fd, 0x6a, 0x0f, &r0f) == 0 &&
	    i2c_read_reg(fd, 0x6a, 0x11, &r11) == 0) {
		result(R_INFO, "BQ-status", "VBUS=%s, PG=%u, charge=%s, fault=0x%02x",
		       vbus_names[(r0b >> 5) & 7], (r0b >> 2) & 1,
		       charge_names[(r0b >> 3) & 3], r0c);
		result(R_INFO, "BQ-voltage", "VBAT~%u mV, VSYS~%u mV, VBUS~%u mV (ADC-derived)",
		       2304 + (r0e & 0x7f) * 20, 2304 + (r0f & 0x7f) * 20,
		       2600 + (r11 & 0x7f) * 100);
		if (r0c)
			result(R_WARN, "BQ-fault", "fault register is non-zero: 0x%02x; check battery/VBUS conditions", r0c);
	} else {
		result(R_WARN, "BQ-status", "chip ID works, but one or more status reads failed");
	}
}

static void test_ea3056(int fd)
{
	uint8_t regs[8];
	int i;
	for (i = 0; i < (int)ARRAY_SIZE(regs); ++i) {
		if (i2c_read_reg(fd, 0x35, (uint8_t)i, &regs[i]) < 0) {
			result(R_FAIL, "EA3056@0x35", "register 0x%02x did not respond", i);
			return;
		}
	}
	result(R_PASS, "EA3056@0x35", "read-only register access OK: %02x %02x %02x %02x %02x %02x %02x %02x",
	       regs[0], regs[1], regs[2], regs[3], regs[4], regs[5], regs[6], regs[7]);
	result(R_INFO, "EA3056-rails", "CPU is running from its rails; exact TP voltage still requires a multimeter");
}

static void test_i2c(void)
{
	int fd;
	section("3. TWI0 / I2C DEVICES");
	fd = open("/dev/i2c-0", O_RDWR);
	if (fd < 0) {
		result(R_FAIL, "twi0", "cannot open /dev/i2c-0: %s", strerror(errno));
		return;
	}
	result(R_PASS, "twi0", "/dev/i2c-0 opened at the board's conservative 100 kHz setting");
	test_dht30(fd);
	test_bq25895(fd);
	test_ea3056(fd);
	close(fd);
}

static int serial_open(const char *path)
{
	struct termios tio;
	int fd = open(path, O_RDWR | O_NOCTTY | O_NONBLOCK);
	if (fd < 0)
		return -1;
	memset(&tio, 0, sizeof(tio));
	tio.c_cflag = B115200 | CS8 | CLOCAL | CREAD;
	tio.c_iflag = IGNPAR;
	tio.c_oflag = 0;
	tio.c_lflag = 0;
	tio.c_cc[VMIN] = 0;
	tio.c_cc[VTIME] = 0;
	cfsetispeed(&tio, B115200);
	cfsetospeed(&tio, B115200);
	if (tcsetattr(fd, TCSANOW, &tio) < 0) {
		close(fd);
		return -1;
	}
	tcflush(fd, TCIOFLUSH);
	return fd;
}

static void serial_drain(int fd, int quiet_ms, int limit_ms)
{
	char discard[256];
	long long deadline = monotonic_ms() + limit_ms;

	while (monotonic_ms() < deadline) {
		fd_set rfds;
		struct timeval tv;
		int rc;
		FD_ZERO(&rfds);
		FD_SET(fd, &rfds);
		tv.tv_sec = quiet_ms / 1000;
		tv.tv_usec = (quiet_ms % 1000) * 1000;
		rc = select(fd + 1, &rfds, NULL, NULL, &tv);
		if (rc <= 0)
			break;
		while (read(fd, discard, sizeof(discard)) > 0)
			;
	}
}

static bool response_complete(const char *response)
{
	return strstr(response, "\r\nOK\r\n") ||
	       strstr(response, "\nOK\r\n") ||
	       strstr(response, "\r\nERROR\r\n") ||
	       strstr(response, "+CME ERROR:") ||
	       strstr(response, "+CMS ERROR:");
}

static int at_command(int fd, const char *command, char *response, size_t response_size, int timeout_ms)
{
	char request[128];
	size_t used = 0;
	long long deadline;

	if (response_size < 2)
		return -1;
	response[0] = '\0';
	serial_drain(fd, 120, 600);
	tcflush(fd, TCIFLUSH);
	snprintf(request, sizeof(request), "%s\r", command);
	if (write(fd, request, strlen(request)) != (ssize_t)strlen(request))
		return -1;
	deadline = monotonic_ms() + timeout_ms;
	while (monotonic_ms() < deadline && used + 1 < response_size) {
		fd_set rfds;
		struct timeval tv;
		int rc;
		ssize_t n;
		FD_ZERO(&rfds);
		FD_SET(fd, &rfds);
		tv.tv_sec = 0;
		tv.tv_usec = 200000;
		rc = select(fd + 1, &rfds, NULL, NULL, &tv);
		if (rc < 0 && errno != EINTR)
			break;
		if (rc <= 0)
			continue;
		n = read(fd, response + used, response_size - used - 1);
		if (n > 0) {
			used += (size_t)n;
			response[used] = '\0';
			if (response_complete(response))
				break;
		}
	}
	return used > 0 ? 0 : -1;
}

static void compact_response(char *s)
{
	char *src = s, *dst = s;
	bool last_space = true;
	while (*src) {
		unsigned char c = (unsigned char)*src++;
		if (c == '\r' || c == '\n' || c == '\t')
			c = ' ';
		if (c < 32 || c > 126)
			continue;
		if (c == ' ') {
			if (last_space)
				continue;
			last_space = true;
		} else {
			last_space = false;
		}
		*dst++ = (char)c;
	}
	while (dst > s && dst[-1] == ' ')
		--dst;
	*dst = '\0';
}

static bool response_ok(const char *response)
{
	return strstr(response, "OK") != NULL &&
	       strstr(response, "ERROR") == NULL &&
	       strstr(response, "+CME ERROR:") == NULL &&
	       strstr(response, "+CMS ERROR:") == NULL;
}

static bool report_at(int fd, const char *label, const char *command, const char *expected,
		      int timeout_ms, char *out, size_t out_size)
{
	int attempt;

	for (attempt = 0; attempt < 2; ++attempt) {
		if (at_command(fd, command, out, out_size, timeout_ms) < 0) {
			out[0] = '\0';
			continue;
		}
		compact_response(out);
		if (response_ok(out) && (!expected || strstr(out, expected))) {
			result(R_PASS, label, "%s", out[0] ? out : "empty response");
			return true;
		}
		if (strstr(out, "ERROR"))
			break;
		serial_drain(fd, 150, 800);
	}
	if (!out[0])
		result(R_WARN, label, "%s returned no data", command);
	else if (expected && !strstr(out, expected))
		result(R_WARN, label, "response did not match %s: %s", expected, out);
	else
		result(R_WARN, label, "%s", out);
	return false;
}

static int open_air780eg(void)
{
	int fd = -1;
	char response[1024];
	int i;
	bool alive = false;

	if (g_gpio_base < 0)
		g_gpio_base = find_gpio_base();
	if (g_gpio_base < 0 || gpio_direction(GPIO_PD21, 0) < 0 || gpio_direction(GPIO_PE5, 0) < 0) {
		result(R_FAIL, "Air-control", "cannot put PWRKEY/RESET controls into inactive state");
		return -1;
	}
	result(R_PASS, "Air-safe-state", "PWRKEY inactive; RESET_N was not pulsed and will remain untouched");
	if (gpio_direction(GPIO_PE1, 1) < 0) {
		result(R_FAIL, "Air-3V9", "cannot assert PE1/4G_EN");
		return -1;
	}
	result(R_PASS, "Air-3V9", "PE1/4G_EN asserted; verify TP7 is about 3.9 V with a multimeter");
	usleep(500000);
	fd = serial_open("/dev/ttyS1");
	if (fd < 0) {
		result(R_FAIL, "Air-UART", "cannot open /dev/ttyS1 at 115200 8N1: %s", strerror(errno));
		return -1;
	}
	if (flock(fd, LOCK_EX | LOCK_NB) < 0) {
		result(R_FAIL, "Air-UART", "/dev/ttyS1 is already in use by another test");
		close(fd);
		return -1;
	}
	result(R_PASS, "Air-UART", "/dev/ttyS1 configured as 115200 8N1, flow control off");

	/* First probe without touching PWRKEY, so an already-running modem is never toggled off. */
	for (i = 0; i < 3; ++i) {
		if (at_command(fd, "AT", response, sizeof(response), 800) == 0 && response_ok(response)) {
			alive = true;
			break;
		}
		usleep(300000);
	}
	if (!alive) {
		/* 1.2 s is above the documented boot threshold but below shutdown hold time. */
		result(R_INFO, "Air-PWRKEY", "no AT response yet; applying one 1.2-second boot pulse");
		if (gpio_direction(GPIO_PD21, 1) < 0) {
			result(R_FAIL, "Air-PWRKEY", "cannot assert PD21");
			close(fd);
			return -1;
		}
		usleep(1200000);
		gpio_direction(GPIO_PD21, 0);
		for (i = 0; i < 12; ++i) {
			usleep(1000000);
			if (at_command(fd, "AT", response, sizeof(response), 800) == 0 && response_ok(response)) {
				alive = true;
				break;
			}
		}
	}
	if (!alive) {
		result(R_FAIL, "Air-AT", "no AT response after power enable and safe PWRKEY boot pulse");
		result(R_MANUAL, "Air-debug", "measure TP7=3.9 V and TP_VDD_EXT; also inspect U12 debug UART");
		close(fd);
		return -1;
	}
	result(R_PASS, "Air-AT", "modem answered AT after %d startup wait cycle(s)", i + 1);
	return fd;
}

static void test_air780eg(void)
{
	int fd;
	char response[1024];
	int rssi = 99, ber = 99;
	int cereg_mode = -1, cereg_stat = -1;
	bool cereg_ok;

	section("4. AIR780EG / SIM / LTE");
	fd = open_air780eg();
	if (fd < 0)
		return;
	report_at(fd, "Air-model", "ATI", "AirM2M", 1500, response, sizeof(response));
	report_at(fd, "SIM", "AT+CPIN?", "+CPIN:", 1500, response, sizeof(response));
	if (strstr(response, "READY"))
		result(R_PASS, "SIM-ready", "SIM interface and card authentication report READY");
	else
		result(R_WARN, "SIM-ready", "not READY; insert/unlock a SIM before judging the SIM socket");
	report_at(fd, "LTE-signal", "AT+CSQ", "+CSQ:", 1500, response, sizeof(response));
	if (sscanf(strstr(response, "+CSQ:") ? strstr(response, "+CSQ:") : "", "+CSQ: %d,%d", &rssi, &ber) == 2) {
		if (rssi == 99)
			result(R_WARN, "LTE-RF", "RSSI is unknown (99); check LTE antenna, SIM and coverage");
		else
			result(R_PASS, "LTE-RF", "modem reports RSSI index %d (antenna path needs a real network test)", rssi);
	}
	cereg_ok = report_at(fd, "LTE-register", "AT+CEREG?", "+CEREG:", 1500,
			     response, sizeof(response));
	if (cereg_ok) {
		char *cereg = strstr(response, "+CEREG:");
		int fields = cereg ? sscanf(cereg, "+CEREG: %d,%d", &cereg_mode, &cereg_stat) : 0;

		/* Some firmware returns only <stat>, while query mode normally returns <n>,<stat>. */
		if (fields == 1)
			cereg_stat = cereg_mode;
		else if (fields != 2)
			cereg_stat = -1;
	}
	if (cereg_ok && (cereg_stat == 1 || cereg_stat == 5))
		result(R_PASS, "LTE-network", "registered on a network");
	else
		result(R_WARN, "LTE-network", "not registered now; this can be normal without SIM/antenna/coverage");
	result(R_MANUAL, "GNSS-RF", "a real GNSS antenna test requires outdoor sky view and a position fix");
	close(fd);
}

static bool nonzero_coordinate(const char *s)
{
	while (*s == '-' || *s == '+' || *s == '0' || *s == '.')
		++s;
	return *s != '\0' && *s != ',' && *s != ' ';
}

static void test_gnss(void)
{
	int fd;
	char response[2048];
	long long deadline;
	int poll = 0;
	bool fixed = false;

	section("AIR780EG GNSS OUTDOOR FIX TEST");
	fd = open_air780eg();
	if (fd < 0)
		return;
	if (!report_at(fd, "GNSS-power-on", "AT+CGNSPWR=1", NULL, 2500,
		       response, sizeof(response))) {
		close(fd);
		return;
	}
	g_gnss_stop = 0;
	signal(SIGINT, request_gnss_stop);
	signal(SIGTERM, request_gnss_stop);
	result(R_INFO, "GNSS-wait", "polling for up to 5 minutes; place the GNSS antenna outdoors with open sky");
	deadline = monotonic_ms() + 300000;
	while (!g_gnss_stop && monotonic_ms() < deadline) {
		char *p;
		int run = 0, fix = 0;
		char utc[40] = { 0 }, lat[40] = { 0 }, lon[40] = { 0 };
		++poll;
		if (at_command(fd, "AT+CGNSINF", response, sizeof(response), 2500) == 0) {
			compact_response(response);
			p = strstr(response, "+CGNSINF:");
			if (p && sscanf(p, "+CGNSINF: %d,%d,%39[^,],%39[^,],%39[^,]",
					&run, &fix, utc, lat, lon) == 5) {
				if (fix == 1 && nonzero_coordinate(lat) && nonzero_coordinate(lon)) {
					result(R_PASS, "GNSS-fix", "UTC=%s latitude=%s longitude=%s", utc, lat, lon);
					fixed = true;
					break;
				}
				if (poll == 1 || poll % 8 == 0)
					result(R_INFO, "GNSS-search", "run=%d fix=%d UTC=%s latitude=%s longitude=%s",
					       run, fix, utc[0] ? utc : "none", lat[0] ? lat : "none", lon[0] ? lon : "none");
			} else if (poll == 1 || poll % 8 == 0) {
				result(R_WARN, "GNSS-response", "unexpected response: %s", response);
			}
		}
		if (g_gnss_stop || monotonic_ms() + 2000 >= deadline)
			break;
		sleep(2);
	}
	if (g_gnss_stop)
		result(R_WARN, "GNSS-cancel", "test interrupted; switching GNSS off before exit");
	else if (!fixed)
		result(R_WARN, "GNSS-fix", "no position fix within 5 minutes; check outdoor sky view and GNSS antenna path");
	if (!report_at(fd, "GNSS-power-off", "AT+CGNSPWR=0", NULL, 2500,
		       response, sizeof(response)))
		result(R_WARN, "GNSS-cleanup", "GNSS may still be powered; retry AT+CGNSPWR=0");
	signal(SIGINT, SIG_DFL);
	signal(SIGTERM, SIG_DFL);
	close(fd);
}

static int first_dir_entry(const char *path, char *name, size_t size)
{
	DIR *dir = opendir(path);
	struct dirent *de;
	if (!dir)
		return -1;
	while ((de = readdir(dir)) != NULL) {
		if (de->d_name[0] == '.')
			continue;
		snprintf(name, size, "%s", de->d_name);
		closedir(dir);
		return 0;
	}
	closedir(dir);
	return -1;
}

static int make_dir(const char *path)
{
	return mkdir(path, 0755) == 0 || errno == EEXIST ? 0 : -1;
}

static void test_usb1_host(void)
{
	DIR *dir;
	struct dirent *de;
	int roots = 0, devices = 0;

	if (g_gpio_base < 0)
		g_gpio_base = find_gpio_base();
	if (g_gpio_base >= 0 && gpio_direction(GPIO_PE0, 1) == 0) {
		result(R_PASS, "USB1-5V-enable", "PE0/CAM_EN asserted; verify TP6/CN2 VBUS is about 5 V");
		usleep(1500000);
	} else {
		result(R_FAIL, "USB1-5V-enable", "cannot assert PE0/CAM_EN");
	}
	dir = opendir("/sys/bus/usb/devices");
	if (dir) {
		while ((de = readdir(dir)) != NULL) {
			char *dash;
			if (!strncmp(de->d_name, "usb", 3))
				++roots;
			dash = strchr(de->d_name, '-');
			if (dash && dash[1] && dash[1] != '0' && !strchr(de->d_name, ':'))
				++devices;
		}
		closedir(dir);
	}
	result(roots > 0 ? R_PASS : R_FAIL, "USB1-host", "USB root hubs=%d, attached non-root devices=%d", roots, devices);
	if (devices > 0)
		result(R_PASS, "USB1-data", "at least one external USB device enumerated");
	else
		result(R_MANUAL, "USB1-data", "insert a known-good USB flash drive into CN2 and rerun this command");
	if (g_gpio_base >= 0 && gpio_direction(GPIO_PE0, 0) == 0)
		result(R_INFO, "USB1-power", "PE0/CAM_EN returned low after enumeration check");
}

static void test_usb0_device(void)
{
	char udc[128] = { 0 }, state[128] = { 0 }, path[PATH_MAX];
	char bound[128] = { 0 };

	if (first_dir_entry("/sys/class/udc", udc, sizeof(udc)) < 0) {
		result(R_FAIL, "USB0-UDC", "no USB device controller appeared in /sys/class/udc");
		return;
	}
	result(R_PASS, "USB0-UDC", "device controller present: %s", udc);
	if (read_file("/sys/kernel/config/usb_gadget/g1/UDC", bound, sizeof(bound)) > 0)
		result(R_PASS, "USB0-ADB", "ADB gadget is bound to %s", bound);
	else
		result(R_WARN, "USB0-ADB", "ADB gadget is not bound; run usb0-status for startup details");
	snprintf(path, sizeof(path), "/sys/class/udc/%s/state", udc);
	read_file(path, state, sizeof(state));
	if (!strcmp(state, "configured"))
		result(R_PASS, "USB0-data", "external host configured ADB; Type-C data path works");
	else
		result(R_MANUAL, "USB0-data", "UDC state=%s; connect TYPE_C1 with a data cable and run usb0-status",
		       state[0] ? state : "unknown");
}

static void test_usb(void)
{
	section("5. USB0 DEVICE / USB1 HOST");
	test_usb1_host();
	test_usb0_device();
	result(R_MANUAL, "Air-USB-CN3", "CN3 is wired directly to Air780EG, not T113; verify its USB enumeration from a PC");
}

static void print_manual_checks(void)
{
	section("6. PHYSICAL CHECKS THAT SOFTWARE CANNOT PROVE");
	result(R_MANUAL, "power-rails", "measure TP2/TP3/TP4/TP5/TP6/TP7/TP8 with a multimeter");
	result(R_MANUAL, "buttons", "press T113 RESET, Air RESET and BQ25895 QON buttons one at a time");
	result(R_MANUAL, "antennas", "LTE needs network registration; GNSS needs an outdoor position fix");
	result(R_MANUAL, "connectors", "wiggle-test TF, SIM, TYPE_C1, CN2, CN3 and UART headers under operation");
}

int main(int argc, char **argv)
{
	const char *program = strrchr(argv[0], '/');
	bool air_only, gnss_only;
	const char *tmp_log = "/tmp/mosquito-board-test.log";
	const char *persistent_name = "board-test.log";
	char persistent_path[PATH_MAX];

	program = program ? program + 1 : argv[0];
	air_only = !strcmp(program, "air-test") ||
		(argc == 2 && !strcmp(argv[1], "air"));
	gnss_only = !strcmp(program, "gnss-test") ||
		(argc == 2 && !strcmp(argv[1], "gnss"));
	setvbuf(stdout, NULL, _IOLBF, 0);
	if (air_only) {
		tmp_log = "/tmp/mosquito-air-test.log";
		persistent_name = "air-test.log";
		g_log = fopen(tmp_log, "w");
		emit("Mosquito T113-S3 Air780EG LTE/SIM diagnostic v2.3\n");
		emit("Safe policy: one PWRKEY boot pulse is allowed only when needed; RESET_N is never pulsed.\n");
		test_air780eg();
		section("SUMMARY");
		emit("PASS=%d  FAIL=%d  WARN=%d  MANUAL=%d\n", g_pass, g_fail, g_warn, g_manual);
	} else if (gnss_only) {
		tmp_log = "/tmp/mosquito-gnss-test.log";
		persistent_name = "gnss-test.log";
		g_log = fopen(tmp_log, "w");
		emit("Mosquito T113-S3 Air780EG GNSS diagnostic v2.3\n");
		emit("Safe policy: RESET_N is never pulsed; GNSS is switched off when the test finishes.\n");
		test_gnss();
		section("SUMMARY");
		emit("PASS=%d  FAIL=%d  WARN=%d  MANUAL=%d\n", g_pass, g_fail, g_warn, g_manual);
	} else {
		g_log = fopen(tmp_log, "w");
		emit("Mosquito T113-S3 whole-board diagnostic v2.3\n");
		emit("Safe policy: no storage writes outside a temporary overlay test; Air RESET_N is never pulsed.\n");
		test_board_basics();
		test_status_gpios();
		test_i2c();
		test_air780eg();
		test_usb();
		print_manual_checks();
		section("SUMMARY");
		emit("PASS=%d  FAIL=%d  WARN=%d  MANUAL=%d\n", g_pass, g_fail, g_warn, g_manual);
		if (g_fail == 0)
			emit("Automatic checks completed without a hard failure. Finish every MANUAL item before calling the PCB fully verified.\n");
		else
			emit("There are %d hard failure(s). Fix those before continuing with stress tests.\n", g_fail);
	}
	emit("Temporary report: %s\n", tmp_log);
	if (g_log)
		fflush(g_log);
	make_dir("/overlay/mosquito-test");
	snprintf(persistent_path, sizeof(persistent_path), "/overlay/mosquito-test/%s", persistent_name);
	if (copy_file(tmp_log, persistent_path) == 0)
		emit("Persistent report: %s\n", persistent_path);
	if (!air_only && !gnss_only)
		copy_file(tmp_log, "/overlay/mosquito-board-test.log");
	if (g_log)
		fclose(g_log);
	return g_fail ? 1 : 0;
}
