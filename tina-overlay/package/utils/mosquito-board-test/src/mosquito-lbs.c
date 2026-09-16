#define _GNU_SOURCE

#include <ctype.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <poll.h>
#include <signal.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/file.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

#define DEFAULT_TTY "/dev/ttyS1"
#define LOCK_DIR "/var/run/mosquito-air780eg-start.lock"
#define RESPONSE_SIZE 8192

enum at_result {
	AT_OK = 0,
	AT_ERROR = 1,
	AT_TIMEOUT = 2,
	AT_IO = 3,
	AT_INTERRUPTED = 4
};

struct serial_port {
	int fd;
	struct termios saved;
	bool saved_valid;
};

struct bearer_profile {
	char contype[32];
	char apn[128];
	bool have_contype;
	bool have_apn;
	bool changed;
};

struct lbs_fix {
	int code;
	double latitude;
	double longitude;
	char date[16];
	char time[16];
};

/* One LTE cell observation from AT+CCED (serving cell or neighbour). No IMSI/ICCID is kept. */
#define CELL_MAX 16

struct cell_info {
	bool serving;
	int mcc;
	int mnc;
	long earfcn;
	long cell_id;
	long tac;
	int pci;
	int rsrp_dbm;
	int rsrq_db10; /* tenths of dB */
	long rsrp_raw; /* value as printed by the modem, kept for server-side calibration */
};

struct cell_set {
	struct cell_info items[CELL_MAX];
	int count;
	bool queried;
};

/* Parsed AT+CGNSINF line (Air780EG built-in GNSS). */
struct gnss_fix {
	int run_status;
	int fix_status;
	char utc[24];
	double latitude;
	double longitude;
	double altitude;
	int fix_mode;
	double hdop;
	int sats_view;
	int sats_used;
	int glonass_used;
	int cn0_max;
};

static volatile sig_atomic_t interrupted;
static int transcript_fd = -1;
static bool own_lock;

static void on_signal(int signo)
{
	(void)signo;
	interrupted = 1;
}

static long long monotonic_ms(void)
{
	struct timespec ts;
	if (clock_gettime(CLOCK_MONOTONIC, &ts) < 0)
		return 0;
	return (long long)ts.tv_sec * 1000LL + ts.tv_nsec / 1000000LL;
}

static void private_write(const char *data, size_t length)
{
	while (transcript_fd >= 0 && length > 0) {
		ssize_t written = write(transcript_fd, data, length);
		if (written > 0) {
			data += written;
			length -= (size_t)written;
		} else if (written < 0 && errno == EINTR) {
			continue;
		} else {
			break;
		}
	}
}

static void private_command(const char *command)
{
	static const char prefix[] = "\n>>> ";
	private_write(prefix, sizeof(prefix) - 1);
	private_write(command, strlen(command));
	private_write("\n", 1);
}

static char *trim(char *text)
{
	char *end;
	while (*text && isspace((unsigned char)*text))
		++text;
	end = text + strlen(text);
	while (end > text && isspace((unsigned char)end[-1]))
		--end;
	*end = '\0';
	return text;
}

static int terminal_result(const char *response)
{
	char copy[RESPONSE_SIZE];
	char *cursor;
	char *line;

	if (strlen(response) >= sizeof(copy))
		return -1;
	strcpy(copy, response);
	cursor = copy;
	while ((line = strsep(&cursor, "\r\n")) != NULL) {
		line = trim(line);
		if (strcmp(line, "OK") == 0)
			return AT_OK;
		if (strcmp(line, "ERROR") == 0 ||
		    strncmp(line, "+CME ERROR:", 11) == 0 ||
		    strncmp(line, "+CMS ERROR:", 11) == 0)
			return AT_ERROR;
	}
	return -1;
}

static void serial_drain(int fd)
{
	char buffer[256];
	struct pollfd pfd;
	long long deadline = monotonic_ms() + 200;

	pfd.fd = fd;
	pfd.events = POLLIN;
	while (monotonic_ms() < deadline) {
		int rc = poll(&pfd, 1, 20);
		if (rc <= 0)
			continue;
		if (read(fd, buffer, sizeof(buffer)) <= 0 && errno != EINTR)
			break;
	}
}

static int write_all(int fd, const char *data, size_t length, int timeout_ms)
{
	long long deadline = monotonic_ms() + timeout_ms;
	while (length > 0 && !interrupted) {
		struct pollfd pfd;
		int left = (int)(deadline - monotonic_ms());
		ssize_t count;
		if (left <= 0)
			return -1;
		pfd.fd = fd;
		pfd.events = POLLOUT;
		if (poll(&pfd, 1, left > 200 ? 200 : left) <= 0)
			continue;
		count = write(fd, data, length);
		if (count > 0) {
			data += count;
			length -= (size_t)count;
		} else if (count < 0 && errno != EINTR && errno != EAGAIN) {
			return -1;
		}
	}
	return length == 0 ? 0 : -1;
}

static enum at_result at_command(int fd, const char *command, char *response,
				 size_t response_size, int timeout_ms)
{
	char request[256];
	size_t used = 0;
	long long deadline;

	if (snprintf(request, sizeof(request), "%s\r", command) >= (int)sizeof(request))
		return AT_IO;
	serial_drain(fd);
	tcflush(fd, TCIFLUSH);
	private_command(command);
	if (write_all(fd, request, strlen(request), 1000) < 0)
		return interrupted ? AT_INTERRUPTED : AT_IO;
	response[0] = '\0';
	deadline = monotonic_ms() + timeout_ms;
	while (monotonic_ms() < deadline && !interrupted) {
		struct pollfd pfd;
		int left = (int)(deadline - monotonic_ms());
		ssize_t count;
		int terminal;

		pfd.fd = fd;
		pfd.events = POLLIN;
		if (poll(&pfd, 1, left > 250 ? 250 : left) <= 0)
			continue;
		if (!(pfd.revents & (POLLIN | POLLHUP)))
			continue;
		count = read(fd, response + used, response_size - used - 1);
		if (count > 0) {
			private_write(response + used, (size_t)count);
			used += (size_t)count;
			response[used] = '\0';
			terminal = terminal_result(response);
			if (terminal >= 0)
				return (enum at_result)terminal;
			if (used + 1 >= response_size)
				return AT_IO;
		} else if (count < 0 && errno != EINTR && errno != EAGAIN) {
			return AT_IO;
		}
	}
	return interrupted ? AT_INTERRUPTED : AT_TIMEOUT;
}

static bool process_fd_owns_device(const char *tty_path)
{
	struct stat tty_stat;
	DIR *proc;
	struct dirent *process;
	pid_t self = getpid();

	if (stat(tty_path, &tty_stat) < 0)
		return false;
	proc = opendir("/proc");
	if (!proc)
		return false;
	while ((process = readdir(proc)) != NULL) {
		char fd_dir_path[128];
		DIR *fd_dir;
		struct dirent *entry;
		long pid;
		char *end;

		errno = 0;
		pid = strtol(process->d_name, &end, 10);
		if (errno || *end || pid <= 0 || pid == self)
			continue;
		snprintf(fd_dir_path, sizeof(fd_dir_path), "/proc/%ld/fd", pid);
		fd_dir = opendir(fd_dir_path);
		if (!fd_dir)
			continue;
		while ((entry = readdir(fd_dir)) != NULL) {
			char fd_path[512];
			struct stat fd_stat;
			if (!isdigit((unsigned char)entry->d_name[0]))
				continue;
			snprintf(fd_path, sizeof(fd_path), "%s/%s", fd_dir_path, entry->d_name);
			if (stat(fd_path, &fd_stat) == 0 && S_ISCHR(fd_stat.st_mode) &&
			    fd_stat.st_rdev == tty_stat.st_rdev) {
				closedir(fd_dir);
				closedir(proc);
				return true;
			}
		}
		closedir(fd_dir);
	}
	closedir(proc);
	return false;
}

static int acquire_lock(void)
{
	char pid_text[32];
	int pid_fd;
	int length;

	if (access("/sys/class/net/ppp0", F_OK) == 0 ||
	    access("/var/run/ppp-air780eg.pid", F_OK) == 0) {
		fprintf(stderr, "LBS_BLOCKED=PPP_ACTIVE_OR_STARTING\n");
		return -1;
	}
	if (mkdir(LOCK_DIR, 0700) < 0) {
		fprintf(stderr, "LBS_BLOCKED=UART_LOCK_PRESENT\n");
		return -1;
	}
	own_lock = true;
	pid_fd = open(LOCK_DIR "/pid", O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
	if (pid_fd < 0)
		return -1;
	length = snprintf(pid_text, sizeof(pid_text), "%ld\n", (long)getpid());
	if (write(pid_fd, pid_text, (size_t)length) != length) {
		close(pid_fd);
		return -1;
	}
	close(pid_fd);
	return 0;
}

static void release_lock(void)
{
	if (!own_lock)
		return;
	unlink(LOCK_DIR "/pid");
	rmdir(LOCK_DIR);
	own_lock = false;
}

static int serial_open(struct serial_port *port, const char *path)
{
	struct termios configured;

	memset(port, 0, sizeof(*port));
	port->fd = -1;
	if (process_fd_owns_device(path)) {
		errno = EBUSY;
		return -1;
	}
	port->fd = open(path, O_RDWR | O_NOCTTY | O_NONBLOCK | O_CLOEXEC);
	if (port->fd < 0)
		return -1;
	if (flock(port->fd, LOCK_EX | LOCK_NB) < 0)
		goto fail;
	if (tcgetattr(port->fd, &port->saved) < 0)
		goto fail;
	port->saved_valid = true;
	configured = port->saved;
	cfmakeraw(&configured);
	cfsetispeed(&configured, B115200);
	cfsetospeed(&configured, B115200);
	configured.c_cflag |= CLOCAL | CREAD;
	configured.c_cflag &= ~(CSTOPB | PARENB | CSIZE | CRTSCTS | HUPCL);
	configured.c_cflag |= CS8;
	configured.c_cc[VMIN] = 0;
	configured.c_cc[VTIME] = 0;
	if (tcsetattr(port->fd, TCSANOW, &configured) < 0)
		goto fail;
	tcflush(port->fd, TCIOFLUSH);
	return 0;
fail:
	close(port->fd);
	port->fd = -1;
	return -1;
}

static void serial_close(struct serial_port *port)
{
	if (port->fd < 0)
		return;
	if (port->saved_valid)
		tcsetattr(port->fd, TCSANOW, &port->saved);
	flock(port->fd, LOCK_UN);
	close(port->fd);
	port->fd = -1;
}

static bool find_line(const char *response, const char *prefix, char *out, size_t out_size)
{
	const char *start = strstr(response, prefix);
	const char *end;
	size_t length;
	if (!start)
		return false;
	start += strlen(prefix);
	while (*start == ' ' || *start == '\t')
		++start;
	end = start;
	while (*end && *end != '\r' && *end != '\n')
		++end;
	length = (size_t)(end - start);
	if (length >= out_size)
		return false;
	memcpy(out, start, length);
	out[length] = '\0';
	return true;
}

static bool response_contains(const char *response, const char *needle)
{
	return strstr(response, needle) != NULL;
}

static bool query_ok(int fd, const char *label, const char *command, int timeout_ms,
		     char *response, size_t response_size)
{
	enum at_result rc = at_command(fd, command, response, response_size, timeout_ms);
	printf("AT_STEP=%s RESULT=%s\n", label,
	       rc == AT_OK ? "OK" : rc == AT_ERROR ? "MODEM_ERROR" :
	       rc == AT_TIMEOUT ? "TIMEOUT" : rc == AT_INTERRUPTED ? "INTERRUPTED" : "IO_ERROR");
	fflush(stdout);
	return rc == AT_OK;
}

static int parse_cereg(const char *response)
{
	char value[256];
	int mode = -1;
	int status = -1;
	if (!find_line(response, "+CEREG:", value, sizeof(value)))
		return -1;
	if (sscanf(value, "%d,%d", &mode, &status) == 2)
		return status;
	if (sscanf(value, "%d", &status) == 1)
		return status;
	return -1;
}

static int parse_cgatt(const char *response)
{
	char value[32];
	int attached = -1;
	if (find_line(response, "+CGATT:", value, sizeof(value)) &&
	    sscanf(value, "%d", &attached) == 1)
		return attached;
	return -1;
}

static int parse_csq(const char *response)
{
	char value[32];
	int rssi = -1;
	int ber = -1;
	if (find_line(response, "+CSQ:", value, sizeof(value)) &&
	    sscanf(value, "%d,%d", &rssi, &ber) == 2)
		return rssi;
	return -1;
}

static int parse_bearer_status(const char *response)
{
	char value[256];
	int cid = -1;
	int status = -1;
	if (find_line(response, "+SAPBR:", value, sizeof(value)) &&
	    sscanf(value, "%d,%d", &cid, &status) == 2 && cid == 1)
		return status;
	return -1;
}

static bool quoted_tag(const char *response, const char *tag, char *value, size_t value_size)
{
	char pattern[64];
	const char *at;
	const char *begin;
	const char *end;
	size_t length;
	if (snprintf(pattern, sizeof(pattern), "\"%s\"", tag) >= (int)sizeof(pattern))
		return false;
	at = strstr(response, pattern);
	if (!at)
		return false;
	begin = strchr(at + strlen(pattern), ',');
	if (!begin)
		return false;
	begin = strchr(begin, '"');
	if (!begin)
		return false;
	++begin;
	end = strchr(begin, '"');
	if (!end)
		return false;
	length = (size_t)(end - begin);
	if (length >= value_size)
		return false;
	memcpy(value, begin, length);
	value[length] = '\0';
	return true;
}

static void snapshot_profile(const char *response, struct bearer_profile *profile)
{
	memset(profile, 0, sizeof(*profile));
	profile->have_contype = quoted_tag(response, "CONTYPE", profile->contype,
					    sizeof(profile->contype));
	profile->have_apn = quoted_tag(response, "APN", profile->apn, sizeof(profile->apn));
}

static bool safe_profile_value(const char *value)
{
	const unsigned char *p = (const unsigned char *)value;
	for (; *p; ++p)
		if (!isalnum(*p) && *p != '.' && *p != '_' && *p != '-')
			return false;
	return true;
}

static void restore_profile(int fd, struct bearer_profile *profile)
{
	char command[256];
	char response[RESPONSE_SIZE];
	if (!profile->changed)
		return;
	if (profile->have_contype && safe_profile_value(profile->contype)) {
		snprintf(command, sizeof(command), "AT+SAPBR=3,1,\"CONTYPE\",\"%s\"",
			 profile->contype);
		(void)at_command(fd, command, response, sizeof(response), 3000);
	}
	if (profile->have_apn && safe_profile_value(profile->apn)) {
		snprintf(command, sizeof(command), "AT+SAPBR=3,1,\"APN\",\"%s\"", profile->apn);
		(void)at_command(fd, command, response, sizeof(response), 3000);
	}
}

static bool parse_lbs(const char *response, struct lbs_fix *fix)
{
	char value[512];
	char *cursor;
	char *fields[5];
	char *end;
	long code;
	int count = 0;
	int year, month, day, hour, minute, second;

	memset(fix, 0, sizeof(*fix));
	fix->code = -1;
	if (!find_line(response, "+CIPGSMLOC:", value, sizeof(value)))
		return false;
	cursor = value;
	while (count < 5 && (fields[count] = strsep(&cursor, ",")) != NULL) {
		fields[count] = trim(fields[count]);
		++count;
	}
	if (count < 1)
		return false;
	errno = 0;
	code = strtol(fields[0], &end, 10);
	if (errno || *trim(end) || code < 0 || code > 65535)
		return false;
	fix->code = (int)code;
	if (fix->code != 0)
		return true;
	if (count != 5 || cursor != NULL)
		return false;
	errno = 0;
	fix->latitude = strtod(fields[1], &end);
	if (errno || end == fields[1] || *trim(end) || !isfinite(fix->latitude) ||
	    fix->latitude < -90.0 || fix->latitude > 90.0)
		return false;
	errno = 0;
	fix->longitude = strtod(fields[2], &end);
	if (errno || end == fields[2] || *trim(end) || !isfinite(fix->longitude) ||
	    fix->longitude < -180.0 || fix->longitude > 180.0)
		return false;
	if (sscanf(fields[3], "%d/%d/%d", &year, &month, &day) != 3 ||
	    year < 2020 || year > 2200 || month < 1 || month > 12 || day < 1 || day > 31)
		return false;
	if (sscanf(fields[4], "%d:%d:%d", &hour, &minute, &second) != 3 ||
	    hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 60)
		return false;
	snprintf(fix->date, sizeof(fix->date), "%04d-%02d-%02d", year, month, day);
	snprintf(fix->time, sizeof(fix->time), "%02d:%02d:%02d", hour, minute, second);
	return true;
}

/* Air780EG reports LTE RSRP/RSRQ either as 3GPP TS 36.133 indexes (0..97 / 0..34) or as signed dBm/dB. */
static int rsrp_to_dbm(long value)
{
	if (value < 0)
		return (int)value;
	if (value > 97)
		value = 97;
	return (int)(value - 140);
}

static int rsrq_to_db10(long value)
{
	if (value < 0)
		return (int)(value * 10);
	if (value > 34)
		value = 34;
	return (int)(-200 + value * 5);
}

static bool field_long(const char *text, long *out)
{
	char *end;
	if (!text || !*text)
		return false;
	errno = 0;
	*out = strtol(text, &end, 10);
	return !errno && *end == '\0';
}

static int split_fields(char *text, char **fields, int max)
{
	int count = 0;
	char *cursor = text;
	char *field;
	while (count < max && (field = strsep(&cursor, ",")) != NULL)
		fields[count++] = trim(field);
	return count;
}

/* +CCED:LTEcurrentcell:MCC,MNC,imsi,roamingFlag,bandInfo,bandwidth,dlEarfcn,cellid,rsrp,rsrq,tac,SrxLev,pcid */
static bool parse_cced_current(const char *text, struct cell_info *cell)
{
	char copy[512];
	char *fields[16];
	long mcc, mnc, earfcn, cellid, rsrp, rsrq, tac, pci;
	if (strlen(text) >= sizeof(copy))
		return false;
	strcpy(copy, text);
	if (split_fields(copy, fields, 16) < 13)
		return false;
	if (!field_long(fields[0], &mcc) || !field_long(fields[1], &mnc) ||
	    !field_long(fields[6], &earfcn) || !field_long(fields[7], &cellid) ||
	    !field_long(fields[8], &rsrp) || !field_long(fields[9], &rsrq) ||
	    !field_long(fields[10], &tac) || !field_long(fields[12], &pci))
		return false;
	memset(cell, 0, sizeof(*cell));
	cell->serving = true;
	cell->mcc = (int)mcc;
	cell->mnc = (int)mnc;
	cell->earfcn = earfcn;
	cell->cell_id = cellid;
	cell->tac = tac;
	cell->pci = (int)pci;
	cell->rsrp_dbm = rsrp_to_dbm(rsrp);
	cell->rsrq_db10 = rsrq_to_db10(rsrq);
	cell->rsrp_raw = rsrp;
	return cellid > 0 && mcc > 0;
}

/* +CCED:LTEneighborcell:MCC,MNC,frequency,cellid,rsrp,rsrq,tac,SrxLev,pcid */
static bool parse_cced_neighbor(const char *text, struct cell_info *cell)
{
	char copy[512];
	char *fields[16];
	long mcc, mnc, earfcn, cellid, rsrp, rsrq, tac, pci;
	if (strlen(text) >= sizeof(copy))
		return false;
	strcpy(copy, text);
	if (split_fields(copy, fields, 16) < 9)
		return false;
	if (!field_long(fields[0], &mcc) || !field_long(fields[1], &mnc) ||
	    !field_long(fields[2], &earfcn) || !field_long(fields[3], &cellid) ||
	    !field_long(fields[4], &rsrp) || !field_long(fields[5], &rsrq) ||
	    !field_long(fields[6], &tac) || !field_long(fields[8], &pci))
		return false;
	memset(cell, 0, sizeof(*cell));
	cell->serving = false;
	cell->mcc = (int)mcc;
	cell->mnc = (int)mnc;
	cell->earfcn = earfcn;
	cell->cell_id = cellid;
	cell->tac = tac;
	cell->pci = (int)pci;
	cell->rsrp_dbm = rsrp_to_dbm(rsrp);
	cell->rsrq_db10 = rsrq_to_db10(rsrq);
	cell->rsrp_raw = rsrp;
	return cellid > 0 && mcc > 0;
}

static void add_cell(struct cell_set *cells, const struct cell_info *cell)
{
	int i;
	for (i = 0; i < cells->count; ++i) {
		if (cells->items[i].cell_id == cell->cell_id && cells->items[i].mcc == cell->mcc &&
		    cells->items[i].mnc == cell->mnc) {
			if (cell->serving)
				cells->items[i] = *cell;
			return;
		}
	}
	if (cells->count < CELL_MAX)
		cells->items[cells->count++] = *cell;
}

static void parse_cced_response(const char *response, struct cell_set *cells)
{
	char copy[RESPONSE_SIZE];
	char *cursor;
	char *line;
	if (strlen(response) >= sizeof(copy))
		return;
	strcpy(copy, response);
	cursor = copy;
	/*
	 * The manual shows "+CCED:LTEcurrentcell:" but the V2007 firmware prints
	 * "+CCED:LTE current cell: " / "+CCED:LTE neighbor cell: ", so match loosely.
	 */
	while ((line = strsep(&cursor, "\r\n")) != NULL) {
		struct cell_info cell;
		const char *body;
		line = trim(line);
		if (strstr(line, "+CCED:") == NULL || strstr(line, "LTE") == NULL)
			continue;
		body = strstr(line, "cell:");
		if (!body)
			continue;
		body += strlen("cell:");
		while (*body == ' ')
			++body;
		if (strstr(line, "current") != NULL) {
			if (parse_cced_current(body, &cell))
				add_cell(cells, &cell);
		} else if (strstr(line, "neighbor") != NULL || strstr(line, "neighbour") != NULL) {
			if (parse_cced_neighbor(body, &cell))
				add_cell(cells, &cell);
		}
	}
}

/* Reads the serving cell and up to six neighbours. Works whenever the modem is registered; no bearer needed. */
static void collect_cells(int fd, struct cell_set *cells)
{
	char response[RESPONSE_SIZE];
	int serving;
	memset(cells, 0, sizeof(*cells));
	cells->queried = true;
	if (query_ok(fd, "CELL_SERVING", "AT+CCED=0,1", 5000, response, sizeof(response)))
		parse_cced_response(response, cells);
	serving = cells->count;
	if (query_ok(fd, "CELL_NEIGHBORS", "AT+CCED=0,2", 8000, response, sizeof(response)))
		parse_cced_response(response, cells);
	printf("CELLS_SERVING=%d CELLS_NEIGHBORS=%d\n", serving, cells->count - serving);
	fflush(stdout);
}

static int cells_json(char *buffer, size_t size, const struct cell_set *cells)
{
	size_t used;
	int i;
	int n;

	n = snprintf(buffer, size, "[");
	if (n < 0 || (size_t)n >= size)
		return -1;
	used = (size_t)n;
	for (i = 0; i < cells->count; ++i) {
		const struct cell_info *c = &cells->items[i];
		n = snprintf(buffer + used, size - used,
			     "%s{\"serving\":%s,\"mcc\":%d,\"mnc\":%d,\"tac\":%ld,\"cellId\":%ld,\"pci\":%d,"
			     "\"earfcn\":%ld,\"rsrpDbm\":%d,\"rsrpRaw\":%ld,\"rsrqDb\":%s%d.%d}",
			     i ? "," : "", c->serving ? "true" : "false", c->mcc, c->mnc, c->tac, c->cell_id,
			     c->pci, c->earfcn, c->rsrp_dbm, c->rsrp_raw, c->rsrq_db10 < 0 ? "-" : "",
			     abs(c->rsrq_db10) / 10, abs(c->rsrq_db10) % 10);
		if (n < 0 || (size_t)n >= size - used)
			return -1;
		used += (size_t)n;
	}
	n = snprintf(buffer + used, size - used, "]");
	if (n < 0 || (size_t)n >= size - used)
		return -1;
	return (int)(used + (size_t)n);
}

static int write_private_file(const char *path, const char *text, size_t length)
{
	int fd = open(path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
	if (fd < 0)
		return -1;
	if (write(fd, text, length) != (ssize_t)length || fsync(fd) < 0) {
		close(fd);
		unlink(path);
		return -1;
	}
	close(fd);
	return 0;
}

static int write_cells_file(const char *path, const struct cell_set *cells)
{
	char text[2048];
	int length = cells_json(text, sizeof(text) - 1, cells);
	if (length < 0)
		return -1;
	text[length++] = '\n';
	return write_private_file(path, text, (size_t)length);
}

static int write_result(const char *path, const struct lbs_fix *fix, const struct cell_set *cells)
{
	char json[4096];
	char cells_text[2048];
	int length;

	if (cells_json(cells_text, sizeof(cells_text), cells) < 0)
		return -1;
	length = snprintf(json, sizeof(json),
		"{\n"
		"  \"source\": \"LBS_SINGLE_CELL\",\n"
		"  \"crs\": \"WGS84\",\n"
		"  \"latitude\": %.8f,\n"
		"  \"longitude\": %.8f,\n"
		"  \"reported_date\": \"%s\",\n"
		"  \"reported_time\": \"%s\",\n"
		"  \"reported_timezone\": null,\n"
		"  \"accuracy_m\": null,\n"
		"  \"estimated\": true,\n"
		"  \"cells\": %s\n"
		"}\n",
		fix->latitude, fix->longitude, fix->date, fix->time, cells_text);
	if (length <= 0 || length >= (int)sizeof(json))
		return -1;
	return write_private_file(path, json, (size_t)length);
}

static bool parse_cgnsinf(const char *response, struct gnss_fix *fix)
{
	char value[512];
	char *fields[24];
	int count;

	memset(fix, 0, sizeof(*fix));
	if (!find_line(response, "+CGNSINF:", value, sizeof(value)))
		return false;
	count = split_fields(value, fields, 24);
	if (count < 16)
		return false;
	fix->run_status = atoi(fields[0]);
	fix->fix_status = atoi(fields[1]);
	snprintf(fix->utc, sizeof(fix->utc), "%.20s", fields[2]);
	fix->latitude = fields[3][0] ? strtod(fields[3], NULL) : 0.0;
	fix->longitude = fields[4][0] ? strtod(fields[4], NULL) : 0.0;
	fix->altitude = fields[5][0] ? strtod(fields[5], NULL) : 0.0;
	fix->fix_mode = fields[8][0] ? atoi(fields[8]) : 1;
	fix->hdop = fields[10][0] ? strtod(fields[10], NULL) : 0.0;
	fix->sats_view = atoi(fields[14]);
	fix->sats_used = atoi(fields[15]);
	fix->glonass_used = count > 16 && fields[16][0] ? atoi(fields[16]) : 0;
	fix->cn0_max = count > 18 && fields[18][0] ? atoi(fields[18]) : 0;
	return true;
}

static bool gnss_usable(const struct gnss_fix *fix)
{
	return fix->fix_status == 1 && fix->fix_mode >= 2 &&
	       isfinite(fix->latitude) && isfinite(fix->longitude) &&
	       fabs(fix->latitude) <= 90.0 && fabs(fix->longitude) <= 180.0 &&
	       (fix->latitude != 0.0 || fix->longitude != 0.0);
}

static int write_gnss_result(const char *path, const struct gnss_fix *fix, const struct cell_set *cells,
			     bool agnss, int ttff_s)
{
	char json[4096];
	char cells_text[2048];
	int length;

	if (cells_json(cells_text, sizeof(cells_text), cells) < 0)
		return -1;
	length = snprintf(json, sizeof(json),
		"{\n"
		"  \"source\": \"GNSS\",\n"
		"  \"crs\": \"WGS84\",\n"
		"  \"latitude\": %.7f,\n"
		"  \"longitude\": %.7f,\n"
		"  \"altitude_m\": %.1f,\n"
		"  \"hdop\": %.2f,\n"
		"  \"fix_mode\": %d,\n"
		"  \"sats_view\": %d,\n"
		"  \"sats_used\": %d,\n"
		"  \"glonass_used\": %d,\n"
		"  \"cn0_max\": %d,\n"
		"  \"utc\": \"%s\",\n"
		"  \"agnss\": %s,\n"
		"  \"ttff_s\": %d,\n"
		"  \"accuracy_m\": %.1f,\n"
		"  \"estimated\": false,\n"
		"  \"cells\": %s\n"
		"}\n",
		fix->latitude, fix->longitude, fix->altitude, fix->hdop, fix->fix_mode,
		fix->sats_view, fix->sats_used, fix->glonass_used, fix->cn0_max, fix->utc,
		agnss ? "true" : "false", ttff_s, (fix->hdop > 0.0 ? fix->hdop : 1.0) * 5.0, cells_text);
	if (length <= 0 || length >= (int)sizeof(json))
		return -1;
	return write_private_file(path, json, (size_t)length);
}

static bool modem_ready(int fd, bool require_registration)
{
	char response[RESPONSE_SIZE];
	int attempt;
	int cereg = -1;
	int attached = -1;
	int rssi = -1;

	for (attempt = 0; attempt < 10; ++attempt) {
		if (query_ok(fd, "MODEM_AT", "AT", 1500, response, sizeof(response)))
			break;
		usleep(300000);
	}
	if (attempt == 10)
		return false;
	if (!query_ok(fd, "FIRMWARE", "AT+CGMR", 2500, response, sizeof(response)))
		return false;
	if (!query_ok(fd, "SIM", "AT+CPIN?", 2500, response, sizeof(response)) ||
	    !response_contains(response, "+CPIN: READY")) {
		printf("SIM_READY=no\n");
		return false;
	}
	printf("SIM_READY=yes\n");
	if (query_ok(fd, "SIGNAL", "AT+CSQ", 2500, response, sizeof(response)))
		rssi = parse_csq(response);
	printf("CSQ_RSSI_INDEX=%d\n", rssi);
	if (query_ok(fd, "LTE_REGISTRATION", "AT+CEREG?", 2500, response, sizeof(response)))
		cereg = parse_cereg(response);
	printf("LTE_REGISTRATION_STATUS=%d\n", cereg);
	if (query_ok(fd, "PACKET_ATTACH", "AT+CGATT?", 2500, response, sizeof(response)))
		attached = parse_cgatt(response);
	printf("PACKET_ATTACHED=%s\n", attached == 1 ? "yes" : "no");
	if (!query_ok(fd, "LBS_CAPABILITY", "AT+CIPGSMLOC=?", 2500, response, sizeof(response)))
		return false;
	if (!query_ok(fd, "LBS_ENDPOINT", "AT+GSMLOCFG?", 2500, response, sizeof(response)))
		return false;
	if (!response_contains(response, "bs.openluat.com")) {
		printf("LBS_ENDPOINT=unexpected_refused\n");
		return false;
	}
	printf("LBS_ENDPOINT=official_openluat\n");
	if (require_registration && !((cereg == 1 || cereg == 5) && attached == 1))
		return false;
	return true;
}

/* Diagnostic: send the given AT commands in order and print the raw replies (no coordinates involved). */
static int run_at(int fd, char **commands, int count)
{
	char response[RESPONSE_SIZE];
	int i;
	int failures = 0;

	for (i = 0; i < count; ++i) {
		enum at_result rc = at_command(fd, commands[i], response, sizeof(response), 15000);
		printf(">>> %s\n%s\n<<< %s\n", commands[i], response,
		       rc == AT_OK ? "OK" : rc == AT_ERROR ? "MODEM_ERROR" :
		       rc == AT_TIMEOUT ? "TIMEOUT" : rc == AT_INTERRUPTED ? "INTERRUPTED" : "IO_ERROR");
		fflush(stdout);
		if (rc != AT_OK)
			++failures;
		if (interrupted)
			break;
	}
	return failures ? 5 : 0;
}

static int run_probe(int fd)
{
	char response[RESPONSE_SIZE];
	int bearer = -1;
	bool ready = modem_ready(fd, false);
	if (query_ok(fd, "BEARER_STATUS", "AT+SAPBR=2,1", 3000, response, sizeof(response)))
		bearer = parse_bearer_status(response);
	printf("BEARER_STATUS=%d\n", bearer);
	return ready ? 0 : 3;
}

/* Activates the modem-side PDP bearer (cid 1) unless it is already up. Prints "<tag>=..." on failure. */
static bool ensure_bearer(int fd, struct bearer_profile *profile, bool *opened, const char *tag)
{
	char response[RESPONSE_SIZE];
	int bearer = -1;

	*opened = false;
	if (query_ok(fd, "BEARER_PROFILE", "AT+SAPBR=4,1", 3000,
		     response, sizeof(response)))
		snapshot_profile(response, profile);
	if (query_ok(fd, "BEARER_STATUS", "AT+SAPBR=2,1", 3000,
		     response, sizeof(response)))
		bearer = parse_bearer_status(response);
	printf("BEARER_STATUS_BEFORE=%d\n", bearer);
	if (bearer != 1) {
		if (!query_ok(fd, "BEARER_OPEN", "AT+SAPBR=1,1", 60000,
			      response, sizeof(response))) {
			if (!(profile->have_contype && profile->have_apn)) {
				fprintf(stderr, "%s=BEARER_OPEN_FAILED_PROFILE_NOT_RESTORABLE\n", tag);
				return false;
			}
			if (!query_ok(fd, "BEARER_CONTYPE", "AT+SAPBR=3,1,\"CONTYPE\",\"GPRS\"",
				      3000, response, sizeof(response)) ||
			    !query_ok(fd, "BEARER_AUTO_APN", "AT+SAPBR=3,1,\"APN\",\"\"",
				      3000, response, sizeof(response))) {
				fprintf(stderr, "%s=BEARER_CONFIG_FAILED\n", tag);
				return false;
			}
			profile->changed = true;
			if (!query_ok(fd, "BEARER_OPEN_RETRY", "AT+SAPBR=1,1", 60000,
				      response, sizeof(response))) {
				fprintf(stderr, "%s=BEARER_OPEN_FAILED\n", tag);
				return false;
			}
		}
		*opened = true;
	}
	if (!query_ok(fd, "BEARER_VERIFY", "AT+SAPBR=2,1", 5000,
		     response, sizeof(response)) || parse_bearer_status(response) != 1) {
		fprintf(stderr, "%s=BEARER_NOT_CONNECTED\n", tag);
		return false;
	}
	return true;
}

static void close_bearer(int fd, bool opened)
{
	char response[RESPONSE_SIZE];
	if (!opened)
		return;
	if (query_ok(fd, "BEARER_CLOSE", "AT+SAPBR=0,1", 30000,
		     response, sizeof(response)))
		printf("BEARER_CLEANUP=closed\n");
	else
		printf("BEARER_CLEANUP=uncertain\n");
}

/* Polls CEREG/CGATT until the modem is registered and packet-attached (it needs a few seconds after PPP
 * drops from data mode back to AT command mode). Returns true once registered. */
static bool wait_registered(int fd, int attempts)
{
	char response[RESPONSE_SIZE];
	int i;
	for (i = 0; i < attempts && !interrupted; ++i) {
		int cereg = -1;
		int attached = -1;
		if (query_ok(fd, "LTE_REGISTRATION", "AT+CEREG?", 2500, response, sizeof(response)))
			cereg = parse_cereg(response);
		if (query_ok(fd, "PACKET_ATTACH", "AT+CGATT?", 2500, response, sizeof(response)))
			attached = parse_cgatt(response);
		if ((cereg == 1 || cereg == 5) && attached == 1)
			return true;
		usleep(2000000);
	}
	return false;
}

static int run_locate(int fd, const char *output_path, const char *cells_path)
{
	char response[RESPONSE_SIZE];
	struct bearer_profile profile;
	struct cell_set cells;
	struct lbs_fix fix;
	bool opened_bearer = false;
	int rc = 1;

	memset(&profile, 0, sizeof(profile));
	/* Only AT responsiveness is required to read the cell list; registration is needed for the bearer. */
	if (!modem_ready(fd, false)) {
		fprintf(stderr, "LBS_RESULT=PRECONDITION_FAILED\n");
		return 3;
	}
	collect_cells(fd, &cells);
	if (cells_path && write_cells_file(cells_path, &cells) < 0)
		printf("CELLS_OUTPUT=failed\n");
	if (!wait_registered(fd, 10)) {
		fprintf(stderr, "LBS_RESULT=NOT_REGISTERED\n");
		return 3;
	}
	if (!ensure_bearer(fd, &profile, &opened_bearer, "LBS_RESULT"))
		goto cleanup;
	printf("LBS_REQUEST=single_free_lookup_started\n");
	if (!query_ok(fd, "LBS_LOOKUP", "AT+CIPGSMLOC=1,1", 50000,
		     response, sizeof(response))) {
		fprintf(stderr, "LBS_RESULT=REQUEST_FAILED\n");
		goto cleanup;
	}
	if (!parse_lbs(response, &fix)) {
		fprintf(stderr, "LBS_RESULT=MALFORMED_RESPONSE\n");
		goto cleanup;
	}
	if (fix.code != 0) {
		fprintf(stderr, "LBS_RESULT=SERVICE_ERROR CODE=%d\n", fix.code);
		rc = fix.code == 408 ? 4 : 5;
		goto cleanup;
	}
	if (write_result(output_path, &fix, &cells) < 0) {
		fprintf(stderr, "LBS_RESULT=PRIVATE_OUTPUT_FAILED\n");
		goto cleanup;
	}
	printf("LBS_RESULT=SUCCESS PRIVATE_OUTPUT=%s\n", output_path);
	rc = 0;
cleanup:
	close_bearer(fd, opened_bearer);
	restore_profile(fd, &profile);
	return rc;
}

/*
 * Built-in GNSS with optional AGNSS (time + EPO + coarse position pushed by the modem over its own
 * bearer). Progress lines never contain coordinates; the fix goes to the private output file only.
 */
static int run_gnss(int fd, const char *output_path, const char *cells_path, int timeout_s, bool agnss)
{
	char response[RESPONSE_SIZE];
	struct bearer_profile profile;
	struct cell_set cells;
	struct gnss_fix fix;
	struct gnss_fix last;
	long long start;
	long long deadline;
	long long last_report = 0;
	bool opened_bearer = false;
	bool have_fix = false;
	int rc = 1;

	memset(&profile, 0, sizeof(profile));
	memset(&last, 0, sizeof(last));
	if (!modem_ready(fd, false)) {
		fprintf(stderr, "GNSS_RESULT=PRECONDITION_FAILED\n");
		return 3;
	}
	collect_cells(fd, &cells);
	if (cells_path && write_cells_file(cells_path, &cells) < 0)
		printf("CELLS_OUTPUT=failed\n");
	if (agnss && !ensure_bearer(fd, &profile, &opened_bearer, "AGNSS_BEARER")) {
		printf("AGNSS=bearer_unavailable\n");
		agnss = false;
	}
	if (!query_ok(fd, "GNSS_POWER_ON", "AT+CGNSPWR=1", 10000, response, sizeof(response))) {
		fprintf(stderr, "GNSS_RESULT=POWER_ON_FAILED\n");
		goto cleanup;
	}
	if (agnss) {
		if (query_ok(fd, "GNSS_AID", "AT+CGNSAID=31,1,1,1", 15000, response, sizeof(response))) {
			printf("AGNSS=requested\n");
		} else {
			printf("AGNSS=rejected\n");
			agnss = false;
		}
	} else {
		printf("AGNSS=disabled\n");
	}
	fflush(stdout);
	start = monotonic_ms();
	deadline = start + (long long)timeout_s * 1000LL;
	while (!interrupted && monotonic_ms() < deadline) {
		if (query_ok(fd, "GNSS_INFO", "AT+CGNSINF", 5000, response, sizeof(response)) &&
		    parse_cgnsinf(response, &fix)) {
			long long now = monotonic_ms();
			last = fix;
			if (now - last_report >= 10000 || gnss_usable(&fix)) {
				printf("GNSS_PROGRESS t=%llds run=%d fix=%d mode=%d view=%d used=%d glonass=%d cn0max=%d hdop=%.1f utc=%s\n",
				       (now - start) / 1000, fix.run_status, fix.fix_status, fix.fix_mode,
				       fix.sats_view, fix.sats_used, fix.glonass_used, fix.cn0_max, fix.hdop,
				       fix.utc[0] ? fix.utc : "none");
				fflush(stdout);
				last_report = now;
			}
			if (gnss_usable(&fix)) {
				have_fix = true;
				break;
			}
		}
		usleep(2000000);
	}
	if (have_fix) {
		int ttff_s = (int)((monotonic_ms() - start) / 1000);
		if (write_gnss_result(output_path, &fix, &cells, agnss, ttff_s) < 0) {
			fprintf(stderr, "GNSS_RESULT=PRIVATE_OUTPUT_FAILED\n");
		} else {
			printf("GNSS_RESULT=SUCCESS TTFF_S=%d MODE=%d USED=%d HDOP=%.1f PRIVATE_OUTPUT=%s\n",
			       ttff_s, fix.fix_mode, fix.sats_used, fix.hdop, output_path);
			rc = 0;
		}
	} else {
		fprintf(stderr, "GNSS_RESULT=NO_FIX TIMEOUT_S=%d LAST view=%d used=%d cn0max=%d\n",
			timeout_s, last.sats_view, last.sats_used, last.cn0_max);
		rc = interrupted ? 130 : 4;
	}
	(void)query_ok(fd, "GNSS_POWER_OFF", "AT+CGNSPWR=0", 10000, response, sizeof(response));
cleanup:
	close_bearer(fd, opened_bearer);
	restore_profile(fd, &profile);
	return rc;
}

static int self_test(void)
{
	struct lbs_fix fix;
	const char success[] = "\r\n+CIPGSMLOC: 0,031.2304160,121.4737010,2026/09/10,11:22:33\r\nOK\r\n";
	const char service_error[] = "\r\n+CIPGSMLOC: 408\r\nOK\r\n";
	const char invalid[] = "\r\n+CIPGSMLOC: 0,999,121,2026/09/10,11:22:33\r\nOK\r\n";
	if (!parse_lbs(success, &fix) || fix.code != 0 ||
	    fabs(fix.latitude - 31.230416) > 0.0000001 ||
	    fabs(fix.longitude - 121.473701) > 0.0000001)
		return 1;
	if (!parse_lbs(service_error, &fix) || fix.code != 408)
		return 1;
	if (parse_lbs(invalid, &fix))
		return 1;
	{
		/* AT+CCED samples from the Air780E AT manual (IMSI in the serving line is not kept). */
		/* First line is the manual's spacing, the rest is what the V2007 firmware really prints. */
		const char cced[] =
			"\r\n+CCED:LTEcurrentcell:460,00,460045926307603,0,40,n100,39148,140542123,51,29,6334,34,351\r\nOK\r\n"
			"\r\n+CCED:LTE neighbor cell: 460,00,38950,140541985,57,24,6334,36,351\r\n"
			"+CCED:LTE neighbor cell: 460,00,1300,26224401,48,24,6334,27,37\r\n"
			"+CCED:LTE neighbor cell: 460,00,38950,140541985,57,24,6334,36,351\r\nOK\r\n";
		const char cgns[] =
			"\r\n+CGNSINF: 1,1,20201110032427,31.820789,117.117390,78.500,0.00,130.07,3,,1.79,0.89,4.00,,12,11,,,34,,\r\nOK\r\n";
		const char nofix[] = "\r\n+CGNSINF: 1,0,,,,,,,1,,,,,,3,0,,,27,,\r\nOK\r\n";
		struct cell_set cells;
		struct gnss_fix gnss;
		char text[2048];
		memset(&cells, 0, sizeof(cells));
		parse_cced_response(cced, &cells);
		if (cells.count != 3 || !cells.items[0].serving || cells.items[0].cell_id != 140542123 ||
		    cells.items[0].tac != 6334 || cells.items[0].pci != 351 || cells.items[0].rsrp_dbm != -89 ||
		    cells.items[0].rsrq_db10 != -55 || cells.items[1].serving || cells.items[1].cell_id != 140541985 ||
		    cells.items[2].earfcn != 1300 || cells_json(text, sizeof(text), &cells) < 0 ||
		    strstr(text, "\"cellId\":140542123") == NULL || strstr(text, "\"rsrqDb\":-5.5") == NULL)
			return 1;
		if (!parse_cgnsinf(cgns, &gnss) || !gnss_usable(&gnss) || gnss.sats_used != 11 ||
		    gnss.cn0_max != 34 || fabs(gnss.hdop - 1.79) > 0.001 || gnss.fix_mode != 3)
			return 1;
		if (!parse_cgnsinf(nofix, &gnss) || gnss_usable(&gnss) || gnss.sats_view != 3 || gnss.cn0_max != 27)
			return 1;
	}
	printf("SELF_TEST=PASS\n");
	return 0;
}

static void usage(const char *program)
{
	fprintf(stderr,
		"usage: %s --probe|--locate --transcript PATH [--output PATH] [--cells PATH] [--tty PATH]\n"
		"       %s --gnss SECONDS [--no-agnss] --transcript PATH --output PATH [--cells PATH] [--tty PATH]\n"
		"       %s --at 'AT+CMD' [--at ...] --transcript PATH [--tty PATH]\n"
		"       %s --self-test\n", program, program, program, program);
}

int main(int argc, char **argv)
{
	const char *tty_path = DEFAULT_TTY;
	const char *output_path = NULL;
	const char *cells_path = NULL;
	const char *transcript_path = NULL;
	bool probe = false;
	bool locate = false;
	bool gnss = false;
	bool agnss = true;
	int gnss_timeout = 0;
	bool do_self_test = false;
	char *at_commands[32];
	int at_count = 0;
	struct serial_port port;
	struct sigaction action;
	int rc = 1;
	int index;

	for (index = 1; index < argc; ++index) {
		if (strcmp(argv[index], "--probe") == 0)
			probe = true;
		else if (strcmp(argv[index], "--locate") == 0)
			locate = true;
		else if (strcmp(argv[index], "--gnss") == 0 && index + 1 < argc) {
			gnss = true;
			gnss_timeout = atoi(argv[++index]);
		} else if (strcmp(argv[index], "--at") == 0 && index + 1 < argc && at_count < 32)
			at_commands[at_count++] = argv[++index];
		else if (strcmp(argv[index], "--no-agnss") == 0)
			agnss = false;
		else if (strcmp(argv[index], "--self-test") == 0)
			do_self_test = true;
		else if (strcmp(argv[index], "--tty") == 0 && index + 1 < argc)
			tty_path = argv[++index];
		else if (strcmp(argv[index], "--output") == 0 && index + 1 < argc)
			output_path = argv[++index];
		else if (strcmp(argv[index], "--cells") == 0 && index + 1 < argc)
			cells_path = argv[++index];
		else if (strcmp(argv[index], "--transcript") == 0 && index + 1 < argc)
			transcript_path = argv[++index];
		else {
			usage(argv[0]);
			return 2;
		}
	}
	if (do_self_test && !probe && !locate && !gnss && !at_count)
		return self_test();
	if ((probe ? 1 : 0) + (locate ? 1 : 0) + (gnss ? 1 : 0) + (at_count ? 1 : 0) != 1 || !transcript_path ||
	    ((locate || gnss) && !output_path) || (gnss && (gnss_timeout < 10 || gnss_timeout > 3600))) {
		usage(argv[0]);
		return 2;
	}
	umask(0077);
	transcript_fd = open(transcript_path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
	if (transcript_fd < 0) {
		perror("private transcript");
		return 1;
	}
	memset(&action, 0, sizeof(action));
	action.sa_handler = on_signal;
	sigemptyset(&action.sa_mask);
	sigaction(SIGINT, &action, NULL);
	sigaction(SIGTERM, &action, NULL);
	sigaction(SIGHUP, &action, NULL);
	if (acquire_lock() < 0)
		goto done;
	if (serial_open(&port, tty_path) < 0) {
		fprintf(stderr, "LBS_BLOCKED=UART_OPEN_FAILED ERRNO=%d\n", errno);
		goto unlock;
	}
	printf("SERIAL_LOCK=acquired TTY=%s BAUD=115200\n", tty_path);
	if (probe)
		rc = run_probe(port.fd);
	else if (locate)
		rc = run_locate(port.fd, output_path, cells_path);
	else if (gnss)
		rc = run_gnss(port.fd, output_path, cells_path, gnss_timeout, agnss);
	else
		rc = run_at(port.fd, at_commands, at_count);
	serial_close(&port);
unlock:
	release_lock();
done:
	if (transcript_fd >= 0) {
		fsync(transcript_fd);
		close(transcript_fd);
		transcript_fd = -1;
	}
	return interrupted ? 130 : rc;
}
