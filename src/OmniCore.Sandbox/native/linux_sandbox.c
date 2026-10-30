#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <linux/audit.h>
#include <linux/filter.h>
#include <linux/landlock.h>
#include <linux/seccomp.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

static int fail(const char *stage) {
    fprintf(stderr, "OmniCore Linux sandbox setup failed: %s\n", stage);
    return 125;
}

static int path_rule(int ruleset, const char *path, uint64_t access) {
    int fd = open(path, O_PATH | O_CLOEXEC);
    if (fd < 0) return -1;
    struct stat info;
    if (fstat(fd, &info) != 0) { close(fd); return -1; }
    if (!S_ISDIR(info.st_mode))
        access &= LANDLOCK_ACCESS_FS_EXECUTE | LANDLOCK_ACCESS_FS_READ_FILE
            | LANDLOCK_ACCESS_FS_WRITE_FILE | LANDLOCK_ACCESS_FS_TRUNCATE;
    struct landlock_path_beneath_attr rule = { .allowed_access = access, .parent_fd = fd };
    int result = (int)syscall(__NR_landlock_add_rule, ruleset, LANDLOCK_RULE_PATH_BENEATH, &rule, 0);
    close(fd);
    return result;
}

static int limits(void) {
    struct rlimit core = { 0, 0 }, files, processes;
    if (setrlimit(RLIMIT_CORE, &core) != 0 || getrlimit(RLIMIT_NOFILE, &files) != 0
        || getrlimit(RLIMIT_NPROC, &processes) != 0) return -1;
    if (files.rlim_max > 4096) files.rlim_max = 4096;
    if (files.rlim_cur > files.rlim_max) files.rlim_cur = files.rlim_max;
    if (processes.rlim_max > 1024) processes.rlim_max = 1024;
    if (processes.rlim_cur > processes.rlim_max) processes.rlim_cur = processes.rlim_max;
    return setrlimit(RLIMIT_NOFILE, &files) | setrlimit(RLIMIT_NPROC, &processes);
}

#if defined(__x86_64__)
#define OMNI_AUDIT_ARCH AUDIT_ARCH_X86_64
#elif defined(__aarch64__)
#define OMNI_AUDIT_ARCH AUDIT_ARCH_AARCH64
#else
#error Unsupported Linux sandbox architecture
#endif
#define DENY_SYSCALL(nr) BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, (nr), 0, 1), BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | EPERM)

static int seccomp_filter(void) {
    struct sock_filter filter[] = {
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, arch)),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, OMNI_AUDIT_ARCH, 1, 0),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_KILL_PROCESS),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),
#if defined(__x86_64__)
        BPF_JUMP(BPF_JMP | BPF_JGE | BPF_K, 0x40000000, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | EPERM),
#endif
        DENY_SYSCALL(__NR_ptrace),
        DENY_SYSCALL(__NR_mount),
        DENY_SYSCALL(__NR_umount2),
        DENY_SYSCALL(__NR_pivot_root),
        DENY_SYSCALL(__NR_setns),
        DENY_SYSCALL(__NR_unshare),
        DENY_SYSCALL(__NR_bpf),
        DENY_SYSCALL(__NR_perf_event_open),
        DENY_SYSCALL(__NR_open_by_handle_at),
        DENY_SYSCALL(__NR_userfaultfd),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ALLOW)
    };
    struct sock_fprog program = { .len = (unsigned short)(sizeof(filter) / sizeof(filter[0])), .filter = filter };
    return (int)syscall(__NR_seccomp, SECCOMP_SET_MODE_FILTER, 0, &program);
}

int main(int argc, char **argv) {
    int abi = (int)syscall(__NR_landlock_create_ruleset, NULL, 0, LANDLOCK_CREATE_RULESET_VERSION);
    if (abi < 1) return fail("Landlock unavailable");
    uint64_t read = LANDLOCK_ACCESS_FS_EXECUTE | LANDLOCK_ACCESS_FS_READ_FILE | LANDLOCK_ACCESS_FS_READ_DIR;
    uint64_t all = read | LANDLOCK_ACCESS_FS_WRITE_FILE | LANDLOCK_ACCESS_FS_REMOVE_DIR
        | LANDLOCK_ACCESS_FS_REMOVE_FILE | LANDLOCK_ACCESS_FS_MAKE_CHAR | LANDLOCK_ACCESS_FS_MAKE_DIR
        | LANDLOCK_ACCESS_FS_MAKE_REG | LANDLOCK_ACCESS_FS_MAKE_SOCK | LANDLOCK_ACCESS_FS_MAKE_FIFO
        | LANDLOCK_ACCESS_FS_MAKE_BLOCK | LANDLOCK_ACCESS_FS_MAKE_SYM;
    if (abi >= 2) all |= LANDLOCK_ACCESS_FS_REFER;
    if (abi >= 3) all |= LANDLOCK_ACCESS_FS_TRUNCATE;
    struct landlock_ruleset_attr rules = { .handled_access_fs = all };
    int ruleset = (int)syscall(__NR_landlock_create_ruleset, &rules, sizeof(rules), 0);
    if (ruleset < 0) return fail("Landlock ruleset");
    // The outer mount namespace exposes only runtime trees and approved paths.
    if (path_rule(ruleset, "/", read) != 0 || path_rule(ruleset, "/tmp", all) != 0
        || path_rule(ruleset, "/dev/shm", all) != 0) {
        close(ruleset); return fail("Landlock runtime paths");
    }
    const char *devices[] = { "/dev/null", "/dev/zero", "/dev/random", "/dev/urandom" };
    for (size_t device = 0; device < sizeof(devices) / sizeof(devices[0]); device++)
        if (path_rule(ruleset, devices[device], LANDLOCK_ACCESS_FS_READ_FILE | LANDLOCK_ACCESS_FS_WRITE_FILE) != 0) {
            close(ruleset); return fail("Landlock runtime devices");
        }
    int index = 1;
    while (index + 1 < argc && strcmp(argv[index], "--write") == 0) {
        if (path_rule(ruleset, argv[index + 1], all) != 0) { close(ruleset); return fail("Landlock write path"); }
        index += 2;
    }
    if (index + 1 >= argc || strcmp(argv[index], "--") != 0) { close(ruleset); return fail("arguments"); }
    if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0
        || syscall(__NR_landlock_restrict_self, ruleset, 0) != 0) {
        close(ruleset); return fail("Landlock restriction");
    }
    close(ruleset);
    if (limits() != 0 || seccomp_filter() != 0) return fail("resource or syscall restrictions");
    execv(argv[index + 1], argv + index + 1);
    return fail("exec");
}
