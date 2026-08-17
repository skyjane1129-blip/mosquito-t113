#include <stdio.h>
#include <sys/utsname.h>

int main(void)
{
	struct utsname info;

	puts("Hello from Mosquito T113-S3!");
	if (uname(&info) == 0) {
		printf("System: %s %s\n", info.sysname, info.release);
		printf("Machine: %s\n", info.machine);
	}

	return 0;
}
