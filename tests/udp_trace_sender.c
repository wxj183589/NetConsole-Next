// Linux manual benchmark sender. A native absolute-clock pacer avoids Python sleep jitter.
#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <inttypes.h>
#include <netinet/in.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

static uint32_t crc_table[256];

static void init_crc(void) {
    for (uint32_t i = 0; i < 256; ++i) {
        uint32_t c = i;
        for (int bit = 0; bit < 8; ++bit) c = (c & 1) ? (0xedb88320U ^ (c >> 1)) : (c >> 1);
        crc_table[i] = c;
    }
}

static uint32_t crc_update(uint32_t crc, const unsigned char *data, size_t len) {
    for (size_t i = 0; i < len; ++i) crc = crc_table[(crc ^ data[i]) & 0xff] ^ (crc >> 8);
    return crc;
}

static uint64_t ns_now(clockid_t clock_id) {
    struct timespec ts;
    clock_gettime(clock_id, &ts);
    return (uint64_t)ts.tv_sec * 1000000000ULL + (uint64_t)ts.tv_nsec;
}

static void sleep_until(uint64_t target_ns) {
    for (;;) {
        uint64_t now = ns_now(CLOCK_MONOTONIC);
        if (now >= target_ns) return;
        if (target_ns - now > 200000ULL) {
            uint64_t coarse_target = target_ns - 200000ULL;
            struct timespec target = {(time_t)(coarse_target / 1000000000ULL), (long)(coarse_target % 1000000000ULL)};
            while (clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &target, NULL) == EINTR) { }
        } else {
            while (ns_now(CLOCK_MONOTONIC) < target_ns) { }
            return;
        }
    }
}

static int compare_u64(const void *left, const void *right) {
    uint64_t a = *(const uint64_t *)left, b = *(const uint64_t *)right;
    return (a > b) - (a < b);
}

static uint64_t percentile(const uint64_t *sorted, size_t count, double q) {
    if (count == 0) return 0;
    size_t index = (size_t)(count * q + 0.999999) - 1;
    if (index >= count) index = count - 1;
    return sorted[index];
}

int main(int argc, char **argv) {
    if (argc != 6) {
        fprintf(stderr, "usage: sender host port count payload_bytes pps\n");
        return 2;
    }
    const char *host = argv[1];
    int port = atoi(argv[2]);
    uint64_t count = strtoull(argv[3], NULL, 10);
    size_t payload_size = (size_t)strtoul(argv[4], NULL, 10);
    uint64_t pps = strtoull(argv[5], NULL, 10);
    if (count == 0 || payload_size < 128 || payload_size > 65507 || port < 1 || port > 65535) return 2;

    init_crc();
    int fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (fd < 0) { perror("socket"); return 1; }
    int sndbuf = 4 * 1024 * 1024;
    setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &sndbuf, sizeof(sndbuf));
    struct sockaddr_in destination = {0};
    destination.sin_family = AF_INET;
    destination.sin_port = htons((uint16_t)port);
    if (inet_pton(AF_INET, host, &destination.sin_addr) != 1) return 2;

    unsigned char *packet = malloc(payload_size);
    uint64_t *gaps = count > 1 ? malloc((count - 1) * sizeof(uint64_t)) : NULL;
    uint64_t *failed = malloc(count * sizeof(uint64_t));
    if (!packet || (count > 1 && !gaps) || !failed) return 1;
    const uint64_t interval_ns = pps ? 1000000000ULL / pps : 0;
    uint64_t started_mono = ns_now(CLOCK_MONOTONIC), started_wall = ns_now(CLOCK_REALTIME);
    uint64_t next_deadline = started_mono, previous_send = 0;
    size_t failures = 0, gap_count = 0;
    for (uint64_t sequence = 0; sequence < count; ++sequence) {
        if (sequence && interval_ns) sleep_until(next_deadline);
        uint64_t sent_wall = ns_now(CLOCK_REALTIME);
        int prefix_length = snprintf((char *)packet, payload_size, "%016" PRIu64 "|%019" PRIu64 "|%05zu|",
                                     sequence, sent_wall, payload_size);
        if (prefix_length != 43) return 1;
        memset(packet + 52, 'x', payload_size - 52);
        uint32_t crc = 0xffffffffU;
        crc = crc_update(crc, packet, 43);
        crc = crc_update(crc, packet + 52, payload_size - 52);
        snprintf((char *)packet + 43, 9, "%08x", crc ^ 0xffffffffU);
        packet[51] = '|';
        ssize_t result = sendto(fd, packet, payload_size, 0, (struct sockaddr *)&destination, sizeof(destination));
        uint64_t completed = ns_now(CLOCK_MONOTONIC);
        if (previous_send) gaps[gap_count++] = completed - previous_send;
        previous_send = completed;
        if (result != (ssize_t)payload_size) failed[failures++] = sequence;
        if (interval_ns) {
            uint64_t scheduled = next_deadline + interval_ns;
            next_deadline = scheduled > completed + interval_ns ? scheduled : completed + interval_ns;
        }
    }
    uint64_t elapsed_ns = ns_now(CLOCK_MONOTONIC) - started_mono;
    qsort(gaps, gap_count, sizeof(uint64_t), compare_u64);
    size_t below_10us = 0, below_50us = 0;
    for (size_t i = 0; i < gap_count; ++i) {
        below_10us += gaps[i] < 10000;
        below_50us += gaps[i] < 50000;
    }
    printf("{\"attempts\":%" PRIu64 ",\"sendSuccessCalls\":%" PRIu64 ",\"sendFailedSequences\":[",
           count, count - failures);
    for (size_t i = 0; i < failures; ++i) printf("%s%" PRIu64, i ? "," : "", failed[i]);
    printf("],\"startedWallNs\":%" PRIu64 ",\"elapsedSeconds\":%.9f,\"achievedPps\":%.3f,",
           started_wall, elapsed_ns / 1e9, (count - failures) / (elapsed_ns / 1e9));
    printf("\"interSendGapMsP50\":%.6f,\"interSendGapMsP95\":%.6f,\"interSendGapMsP99\":%.6f,\"interSendGapMsMax\":%.6f,",
           percentile(gaps, gap_count, .50) / 1e6, percentile(gaps, gap_count, .95) / 1e6,
           percentile(gaps, gap_count, .99) / 1e6, gap_count ? gaps[gap_count - 1] / 1e6 : 0);
    printf("\"interSendGapsBelow10us\":%zu,\"interSendGapsBelow50us\":%zu}\n", below_10us, below_50us);
    free(failed); free(gaps); free(packet); close(fd);
    return 0;
}
