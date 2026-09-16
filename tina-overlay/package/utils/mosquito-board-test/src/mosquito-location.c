#define _GNU_SOURCE

#include <errno.h>
#include <fcntl.h>
#include <linux/limits.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/file.h>
#include <sys/select.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

#define GPIO_PD21 (3 * 32 + 21)
#define GPIO_PE1  (4 * 32 + 1)

struct fix {
	char utc[32], lat[32], lon[32], alt[32], speed[32], bearing[32], hdop[32];
	int run, valid, mode, view, used, glonass;
};

static long long now_ms(void)
{
	struct timespec ts;
	clock_gettime(CLOCK_MONOTONIC, &ts);
	return (long long)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static int gpio_write(unsigned gpio, const char *name, const char *value)
{
	char path[PATH_MAX];
	int fd;
	if (!name || !value) return -1;
	snprintf(path, sizeof(path), "/sys/class/gpio/gpio%u/%s", gpio, name);
	fd = open(path, O_WRONLY);
	if (fd < 0) return -1;
	if (write(fd, value, strlen(value)) != (ssize_t)strlen(value)) { close(fd); return -1; }
	close(fd);
	return 0;
}

static int gpio_prepare(unsigned gpio, int active)
{
	char path[PATH_MAX], value[32];
	int fd;
	snprintf(path, sizeof(path), "/sys/class/gpio/gpio%u", gpio);
	if (access(path, F_OK) < 0) {
		fd = open("/sys/class/gpio/export", O_WRONLY);
		if (fd < 0) return -1;
		snprintf(value, sizeof(value), "%u", gpio);
		if (write(fd, value, strlen(value)) != (ssize_t)strlen(value)) { close(fd); return -1; }
		close(fd);
		usleep(10000);
	}
	if (gpio_write(gpio, "direction", "out") < 0) return -1;
	return gpio_write(gpio, "value", active ? "1" : "0");
}

static int serial_open(void)
{
	struct termios tio;
	int fd = open("/dev/ttyS1", O_RDWR | O_NOCTTY | O_NONBLOCK);
	if (fd < 0) return -1;
	if (flock(fd, LOCK_EX | LOCK_NB) < 0) { close(fd); return -1; }
	if (tcgetattr(fd, &tio) < 0) { close(fd); return -1; }
	cfmakeraw(&tio);
	cfsetispeed(&tio, B115200); cfsetospeed(&tio, B115200);
	tio.c_cflag |= CLOCAL | CREAD;
	if (tcsetattr(fd, TCSANOW, &tio) < 0) { close(fd); return -1; }
	return fd;
}

static void drain(int fd)
{
	char buf[256];
	fd_set set;
	struct timeval tv = {0, 100000};
	FD_ZERO(&set); FD_SET(fd, &set);
	if (select(fd + 1, &set, NULL, NULL, &tv) > 0) while (read(fd, buf, sizeof(buf)) > 0) { }
}

static int at(int fd, const char *cmd, char *out, size_t size, int timeout_ms)
{
	char req[128];
	ssize_t n;
	long long deadline = now_ms() + timeout_ms;
	fd_set set;
	struct timeval tv;
	if (size < 2) return -1;
	drain(fd); tcflush(fd, TCIFLUSH);
	snprintf(req, sizeof(req), "%s\r", cmd);
	if (write(fd, req, strlen(req)) != (ssize_t)strlen(req)) return -1;
	n = 0; out[0] = '\0';
	while (now_ms() < deadline && (size_t)n + 1 < size) {
		long long left = deadline - now_ms();
		tv.tv_sec = left > 200 ? 0 : 0; tv.tv_usec = (left > 200 ? 200 : left) * 1000;
		FD_ZERO(&set); FD_SET(fd, &set);
		if (select(fd + 1, &set, NULL, NULL, &tv) <= 0) continue;
		if (FD_ISSET(fd, &set)) {
			ssize_t got = read(fd, out + n, size - (size_t)n - 1);
			if (got > 0) { n += got; out[n] = '\0'; if (strstr(out, "\nOK") || strstr(out, "\nERROR")) break; }
		}
	}
	return n > 0 ? 0 : -1;
}

static char *field(char **cursor)
{
	char *p = *cursor, *comma;
	if (!p) return NULL;
	comma = strchr(p, ',');
	if (comma) { *comma = '\0'; *cursor = comma + 1; } else *cursor = NULL;
	while (*p == ' ') ++p;
	return p;
}

static bool number(const char *s, double min, double max)
{
	char *end;
	double v;
	if (!s || !*s) return false;
	errno = 0; v = strtod(s, &end);
	while (*end == ' ') ++end;
	return errno == 0 && end != s && *end == '\0' && v >= min && v <= max;
}

static int parse(const char *response, struct fix *f)
{
	char copy[1800], *p, *v;
	int i = 0;
	memset(f, 0, sizeof(*f)); f->run = f->valid = f->mode = -1;
	f->view = f->used = f->glonass = -1;
	p = strstr(response, "+CGNSINF:"); if (!p) return -1;
	p += 9; snprintf(copy, sizeof(copy), "%s", p); p = copy;
	while (i < 21 && (v = field(&p)) != NULL) {
		switch (i) {
		case 0: f->run = atoi(v); break; case 1: f->valid = atoi(v); break;
		case 2: snprintf(f->utc, sizeof(f->utc), "%s", v); break;
		case 3: snprintf(f->lat, sizeof(f->lat), "%s", v); break;
		case 4: snprintf(f->lon, sizeof(f->lon), "%s", v); break;
		case 5: snprintf(f->alt, sizeof(f->alt), "%s", v); break;
		case 6: snprintf(f->speed, sizeof(f->speed), "%s", v); break;
		case 7: snprintf(f->bearing, sizeof(f->bearing), "%s", v); break;
		case 8: f->mode = atoi(v); break; case 10: snprintf(f->hdop, sizeof(f->hdop), "%s", v); break;
		case 14: f->view = atoi(v); break; case 15: f->used = atoi(v); break; case 16: f->glonass = atoi(v); break;
		}
		++i;
	}
	return i >= 21 ? 0 : -1;
}

static bool usable(const struct fix *f)
{
	return f->run == 1 && f->valid == 1 && (f->mode == 2 || f->mode == 3) &&
		number(f->lat, -90, 90) && number(f->lon, -180, 180) && f->utc[0];
}

int main(void)
{
	char response[2048];
	struct fix f;
	int fd = -1, timeout = 120, i;
	const char *env = getenv("MOSQUITO_LOCATION_TIMEOUT_SEC");
	if (env && atoi(env) >= 10 && atoi(env) <= 600) timeout = atoi(env);
	if (access("/sys/class/net/ppp0", F_OK) == 0 || access("/var/run/ppp-air780eg.pid", F_OK) == 0) {
		fprintf(stderr, "mosquito-location: PPP is active; run 4g-stop first\n"); return 2;
	}
	if (gpio_prepare(GPIO_PE1, 1) < 0 || gpio_prepare(GPIO_PD21, 0) < 0) { fprintf(stderr, "mosquito-location: Air GPIO setup failed\n"); return 1; }
	fd = serial_open();
	if (fd < 0) { fprintf(stderr, "mosquito-location: cannot lock /dev/ttyS1\n"); return 1; }
	for (i = 0; i < 3 && at(fd, "AT", response, sizeof(response), 800) < 0; ++i) usleep(300000);
	if (i == 3) { gpio_prepare(GPIO_PD21, 1); usleep(1200000); gpio_prepare(GPIO_PD21, 0); usleep(3000000); }
	if (at(fd, "AT+CGNSPWR=1", response, sizeof(response), 2500) < 0 || strstr(response, "ERROR")) { fprintf(stderr, "mosquito-location: GNSS power-on failed\n"); close(fd); return 1; }
	for (i = 0; i < timeout; ++i) {
		if (at(fd, "AT+CGNSINF", response, sizeof(response), 2500) == 0 && parse(response, &f) == 0 && usable(&f)) {
			printf("{\"valid\":true,\"utc\":\"%s\",\"lat_wgs84\":%s,\"lon_wgs84\":%s,\"altitude_m\":%s,\"speed_knots\":%s,\"bearing_deg\":%s,\"hdop\":%s,\"sat_view\":%d,\"sat_used\":%d,\"glonass_used\":%d,\"fix_mode\":%d}\n", f.utc, f.lat, f.lon, f.alt[0] ? f.alt : "0", f.speed[0] ? f.speed : "0", f.bearing[0] ? f.bearing : "0", f.hdop[0] ? f.hdop : "0", f.view, f.used, f.glonass, f.mode);
			at(fd, "AT+CGNSPWR=0", response, sizeof(response), 2500); close(fd); return 0;
		}
		sleep(1);
	}
	at(fd, "AT+CGNSPWR=0", response, sizeof(response), 2500); close(fd);
	fprintf(stderr, "mosquito-location: no valid fix within %d seconds\n", timeout); return 3;
}
