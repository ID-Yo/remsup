#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <netinet/in.h>
#include <pthread.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#define MAX_LINE 512
#define MAX_ID 64
#define MAX_OTP 128
#define MAX_PROTOCOL_KEY 32
#define DEFAULT_PORT 8443
#define BAD_PROTOCOL_FAILURE_LIMIT 3
#define BAD_PROTOCOL_BLOCK_SECONDS 1800
#define MAX_CLIENT_WORKERS 256

enum { CHANNEL_COMMAND = 0, CHANNEL_SCREEN = 1 };

static const char *PROTOCOL_KEY_MASK = "LDLDDLLDLDLLDDLDLLDLDLDDLLDLDL";

typedef struct Agent {
    int fd;
    int paired;
    int bridge_finished;
    int alive;
    int channel;
    char id[MAX_ID];
    char otp[MAX_OTP];
    pthread_cond_t cond;
    struct Agent *next;
} Agent;

typedef struct IpFailure {
    char ip[INET_ADDRSTRLEN];
    int failures;
    time_t blocked_until;
    struct IpFailure *next;
} IpFailure;

typedef struct ClientContext {
    int fd;
    int require_proxy;
    char ip[INET_ADDRSTRLEN];
} ClientContext;

static Agent *agents = NULL;
static pthread_mutex_t agents_mutex = PTHREAD_MUTEX_INITIALIZER;
static IpFailure *ip_failures = NULL;
static pthread_mutex_t ip_failures_mutex = PTHREAD_MUTEX_INITIALIZER;
static int client_workers = 0;
static pthread_mutex_t client_workers_mutex = PTHREAD_MUTEX_INITIALIZER;

static void log_line(const char *message) {
    time_t now = time(NULL);
    struct tm tm_now;
    localtime_r(&now, &tm_now);
    char ts[32];
    strftime(ts, sizeof(ts), "%Y-%m-%d %H:%M:%S", &tm_now);
    fprintf(stdout, "%s %s\n", ts, message);
    fflush(stdout);
}

static int read_line(int fd, char *buf, size_t cap) {
    size_t len = 0;
    while (1) {
        char c;
        ssize_t n = recv(fd, &c, 1, 0);
        if (n == 0) return 0;
        if (n < 0) {
            if (errno == EINTR) continue;
            return -1;
        }
        if (c == '\n') break;
        if (c != '\r') {
            if (len + 1 >= cap) return -2;
            buf[len++] = c;
        }
    }
    buf[len] = '\0';
    return (int)len;
}

static void send_text(int fd, const char *text) {
    send(fd, text, strlen(text), 0);
}

static int is_digit_char(char c) {
    return c >= '0' && c <= '9';
}

static int is_letter_char(char c) {
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}

static int is_protocol_key_valid(const char *value) {
    size_t mask_len = strlen(PROTOCOL_KEY_MASK);
    if (strlen(value) != mask_len) return 0;
    for (size_t i = 0; i < mask_len; i++) {
        if (PROTOCOL_KEY_MASK[i] == 'D' && !is_digit_char(value[i])) return 0;
        if (PROTOCOL_KEY_MASK[i] == 'L' && !is_letter_char(value[i])) return 0;
    }
    return 1;
}

static IpFailure *find_ip_failure_locked(const char *ip, int create) {
    IpFailure *cur = ip_failures;
    while (cur) {
        if (strcmp(cur->ip, ip) == 0) return cur;
        cur = cur->next;
    }
    if (!create) return NULL;

    IpFailure *entry = calloc(1, sizeof(IpFailure));
    if (!entry) return NULL;
    snprintf(entry->ip, sizeof(entry->ip), "%s", ip);
    entry->next = ip_failures;
    ip_failures = entry;
    return entry;
}

static int is_ip_blocked(const char *ip) {
    time_t now = time(NULL);
    int blocked = 0;
    pthread_mutex_lock(&ip_failures_mutex);
    IpFailure *entry = find_ip_failure_locked(ip, 0);
    if (entry && entry->blocked_until > now) blocked = 1;
    if (entry && entry->blocked_until <= now && entry->blocked_until != 0) {
        entry->blocked_until = 0;
        entry->failures = 0;
    }
    pthread_mutex_unlock(&ip_failures_mutex);
    return blocked;
}

static void clear_ip_failures(const char *ip) {
    pthread_mutex_lock(&ip_failures_mutex);
    IpFailure *entry = find_ip_failure_locked(ip, 0);
    if (entry) {
        entry->failures = 0;
        entry->blocked_until = 0;
    }
    pthread_mutex_unlock(&ip_failures_mutex);
}

static void record_protocol_failure(const char *ip, const char *reason) {
    time_t now = time(NULL);
    char msg[192];

    pthread_mutex_lock(&ip_failures_mutex);
    IpFailure *entry = find_ip_failure_locked(ip, 1);
    if (!entry) {
        pthread_mutex_unlock(&ip_failures_mutex);
        return;
    }
    if (entry->blocked_until <= now) entry->blocked_until = 0;
    entry->failures++;
    if (entry->failures > BAD_PROTOCOL_FAILURE_LIMIT) {
        entry->blocked_until = now + BAD_PROTOCOL_BLOCK_SECONDS;
        snprintf(msg, sizeof(msg), "blocked ip=%s reason=%s failures=%d", ip, reason, entry->failures);
    } else {
        snprintf(msg, sizeof(msg), "bad protocol ip=%s reason=%s failures=%d", ip, reason, entry->failures);
    }
    pthread_mutex_unlock(&ip_failures_mutex);

    log_line(msg);
}

static void normalize_id(char *s) {
    char out[MAX_ID];
    size_t j = 0;
    for (size_t i = 0; s[i] && j + 1 < sizeof(out); i++) {
        if (s[i] >= '0' && s[i] <= '9') out[j++] = s[i];
    }
    out[j] = '\0';
    snprintf(s, MAX_ID, "%s", out);
}

static void remove_agent_locked(Agent *target) {
    Agent **cur = &agents;
    while (*cur) {
        if (*cur == target) {
            *cur = target->next;
            target->next = NULL;
            target->alive = 0;
            return;
        }
        cur = &((*cur)->next);
    }
}

static int agent_connected(int fd) {
    char byte;
    ssize_t n = recv(fd, &byte, 1, MSG_PEEK | MSG_DONTWAIT);
    return n > 0 || (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR));
}

static Agent *find_registration_locked(const char *id, const char *otp, int channel) {
    Agent *cur = agents;
    while (cur) {
        if (cur->alive && cur->channel == channel && strcmp(cur->id, id) == 0 && strcmp(cur->otp, otp) == 0) return cur;
        cur = cur->next;
    }
    return NULL;
}

static Agent *find_waiting_agent_locked(const char *id, const char *otp, int channel) {
    Agent *cur = agents;
    while (cur) {
        if (cur->alive && !cur->paired && cur->channel == channel && strcmp(cur->id, id) == 0 && strcmp(cur->otp, otp) == 0 && agent_connected(cur->fd)) return cur;
        cur = cur->next;
    }
    return NULL;
}

static void bridge(int a, int b) {
    char buf[8192];
    while (1) {
        fd_set rfds;
        FD_ZERO(&rfds);
        FD_SET(a, &rfds);
        FD_SET(b, &rfds);
        int maxfd = a > b ? a : b;
        int ready = select(maxfd + 1, &rfds, NULL, NULL, NULL);
        if (ready < 0) {
            if (errno == EINTR) continue;
            break;
        }
        if (FD_ISSET(a, &rfds)) {
            ssize_t n = recv(a, buf, sizeof(buf), 0);
            if (n <= 0) break;
            if (send(b, buf, (size_t)n, 0) <= 0) break;
        }
        if (FD_ISSET(b, &rfds)) {
            ssize_t n = recv(b, buf, sizeof(buf), 0);
            if (n <= 0) break;
            if (send(a, buf, (size_t)n, 0) <= 0) break;
        }
    }
}

static void *handle_client_inner(void *arg) {
    ClientContext *ctx = (ClientContext *)arg;
    int fd = ctx->fd;
    char ip[INET_ADDRSTRLEN];
    snprintf(ip, sizeof(ip), "%s", ctx->ip);
    int require_proxy = ctx->require_proxy;
    free(ctx);

    if (require_proxy) {
        char proxy_line[109], protocol[12], source[INET_ADDRSTRLEN], destination[INET_ADDRSTRLEN], extra;
        unsigned source_port = 0, destination_port = 0;
        struct in_addr parsed_source, parsed_destination;
        if (strcmp(ip, "127.0.0.1") != 0 || read_line(fd, proxy_line, sizeof(proxy_line)) <= 0 ||
            sscanf(proxy_line, "PROXY %11s %15s %15s %u %u %c", protocol, source, destination, &source_port, &destination_port, &extra) != 5 ||
            strcmp(protocol, "TCP4") != 0 || inet_pton(AF_INET, source, &parsed_source) != 1 ||
            inet_pton(AF_INET, destination, &parsed_destination) != 1 || source_port < 1 || source_port > 65535 || destination_port < 1 || destination_port > 65535) {
            close(fd);
            return NULL;
        }
        inet_ntop(AF_INET, &parsed_source, ip, sizeof(ip));
    }

    if (is_ip_blocked(ip)) {
        send_text(fd, "ERR blocked\n");
        close(fd);
        return NULL;
    }

    char line[MAX_LINE];
    int n = read_line(fd, line, sizeof(line));
    if (n == -2) {
        record_protocol_failure(ip, "bad-header");
        send_text(fd, "ERR bad-header\n");
        close(fd);
        return NULL;
    }
    if (n <= 0) {
        close(fd);
        return NULL;
    }

    char role[32] = {0};
    char id[MAX_ID] = {0};
    char otp[MAX_OTP] = {0};
    char protocol_key[MAX_PROTOCOL_KEY] = {0};
    char *fields[6] = {0};
    size_t field_count = 0;
    char *save = NULL;
    for (char *field = strtok_r(line, " \t", &save); field && field_count < 6; field = strtok_r(NULL, " \t", &save)) fields[field_count++] = field;
    if (field_count < 4 || field_count > 5 || strlen(fields[0]) >= sizeof(role) || strlen(fields[1]) >= sizeof(id) || strlen(fields[2]) >= sizeof(otp) || strlen(fields[3]) >= sizeof(protocol_key)) {
        record_protocol_failure(ip, "bad-header");
        send_text(fd, "ERR bad-header\n");
        close(fd);
        return NULL;
    }
    snprintf(role, sizeof(role), "%s", fields[0]);
    snprintf(id, sizeof(id), "%s", fields[1]);
    snprintf(otp, sizeof(otp), "%s", fields[2]);
    snprintf(protocol_key, sizeof(protocol_key), "%s", fields[3]);
    int channel = CHANNEL_COMMAND;
    if (field_count == 5) {
        if (strcmp(fields[4], "SCREEN") != 0) channel = -1;
        else channel = CHANNEL_SCREEN;
    }
    if (channel < 0) {
        send_text(fd, "ERR bad-channel\n");
        close(fd);
        return NULL;
    }
    if (!is_protocol_key_valid(protocol_key)) {
        record_protocol_failure(ip, "bad-protocol-key");
        send_text(fd, "ERR bad-protocol-key\n");
        close(fd);
        return NULL;
    }
    clear_ip_failures(ip);
    normalize_id(id);

    if (strcmp(role, "AGENT") == 0) {
        Agent *agent = calloc(1, sizeof(Agent));
        if (!agent) {
            close(fd);
            return NULL;
        }
        agent->fd = fd;
        agent->channel = channel;
        agent->alive = 1;
        snprintf(agent->id, MAX_ID, "%s", id);
        snprintf(agent->otp, MAX_OTP, "%s", otp);
        pthread_cond_init(&agent->cond, NULL);

        pthread_mutex_lock(&agents_mutex);
        if (find_registration_locked(id, otp, channel)) {
            pthread_mutex_unlock(&agents_mutex);
            pthread_cond_destroy(&agent->cond);
            free(agent);
            send_text(fd, "ERR busy\n");
            close(fd);
            return NULL;
        }
        send_text(fd, "WAIT\n");
        agent->next = agents;
        agents = agent;

        char msg[160];
        snprintf(msg, sizeof(msg), "agent online id=%s", agent->id);
        log_line(msg);
        while (!agent->paired && agent_connected(fd)) {
            struct timespec deadline;
            clock_gettime(CLOCK_REALTIME, &deadline);
            deadline.tv_sec++;
            pthread_cond_timedwait(&agent->cond, &agents_mutex, &deadline);
        }
        int paired = agent->paired;
        if (!paired) remove_agent_locked(agent);
        else while (!agent->bridge_finished) pthread_cond_wait(&agent->cond, &agents_mutex);
        if (paired) remove_agent_locked(agent);
        pthread_mutex_unlock(&agents_mutex);
        if (!paired) close(fd);

        pthread_cond_destroy(&agent->cond);
        free(agent);
        return NULL;
    }

    if (strcmp(role, "OPERATOR") == 0) {
        pthread_mutex_lock(&agents_mutex);
        Agent *reservation = find_registration_locked(id, otp, channel);
        Agent *agent = find_waiting_agent_locked(id, otp, channel);
        if (!agent) {
            int active_reservation = reservation && reservation->paired;
            pthread_mutex_unlock(&agents_mutex);
            send_text(fd, active_reservation ? "ERR busy\n" : "ERR no-agent\n");
            close(fd);
            return NULL;
        }
        int agent_fd = agent->fd;
        agent->paired = 1;
        pthread_cond_signal(&agent->cond);
        pthread_mutex_unlock(&agents_mutex);

        char msg[160];
        snprintf(msg, sizeof(msg), "operator paired id=%s", id);
        log_line(msg);
        send_text(fd, "OK\n");
        send_text(agent_fd, "OK\n");
        bridge(fd, agent_fd);
        close(agent_fd);
        close(fd);
        pthread_mutex_lock(&agents_mutex);
        agent->bridge_finished = 1;
        pthread_cond_signal(&agent->cond);
        pthread_mutex_unlock(&agents_mutex);
        log_line("session closed");
        return NULL;
    }

    send_text(fd, "ERR bad-role\n");
    close(fd);
    return NULL;
}

static void *handle_client(void *arg) {
    (void)handle_client_inner(arg);
    pthread_mutex_lock(&client_workers_mutex);
    client_workers--;
    pthread_mutex_unlock(&client_workers_mutex);
    return NULL;
}

static int create_listener(int port, const char *bind_ip) {
    int listener = socket(AF_INET, SOCK_STREAM, 0);
    if (listener < 0) return -1;
    int yes = 1;
    setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof(yes));
    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    if (inet_pton(AF_INET, bind_ip, &addr.sin_addr) != 1) { close(listener); return -1; }
    addr.sin_port = htons((uint16_t)port);
    if (bind(listener, (struct sockaddr *)&addr, sizeof(addr)) < 0 || listen(listener, 64) < 0) { close(listener); return -1; }
    return listener;
}

int main(int argc, char **argv) {
    signal(SIGPIPE, SIG_IGN);
    int port = argc > 1 ? atoi(argv[1]) : DEFAULT_PORT;
    int proxy_port = argc > 3 ? atoi(argv[3]) : 0;
    const char *bind_ip = argc > 2 ? argv[2] : "0.0.0.0";
    if (port < 1 || port > 65535 || proxy_port < 0 || proxy_port > 65535 || proxy_port == port) return 1;
    int listener = create_listener(port, bind_ip);
    int proxy_listener = proxy_port ? create_listener(proxy_port, "127.0.0.1") : -1;
    if (listener < 0 || (proxy_port && proxy_listener < 0)) { perror("relay listen"); if (listener >= 0) close(listener); return 1; }

    char msg[128];
    snprintf(msg, sizeof(msg), "remsup-relay listening on %s:%d; proxy port=%d", bind_ip, port, proxy_port);
    log_line(msg);
    while (1) {
        struct sockaddr_in client_addr;
        socklen_t client_addr_len = sizeof(client_addr);
        fd_set ready;
        FD_ZERO(&ready); FD_SET(listener, &ready);
        int maxfd = listener;
        if (proxy_listener >= 0) { FD_SET(proxy_listener, &ready); if (proxy_listener > maxfd) maxfd = proxy_listener; }
        if (select(maxfd + 1, &ready, NULL, NULL, NULL) < 0) { if (errno == EINTR) continue; break; }
        int use_proxy = proxy_listener >= 0 && FD_ISSET(proxy_listener, &ready);
        int fd = accept(use_proxy ? proxy_listener : listener, (struct sockaddr *)&client_addr, &client_addr_len);
        if (fd < 0) {
            if (errno == EINTR) continue;
            perror("accept");
            continue;
        }
        pthread_mutex_lock(&client_workers_mutex);
        if (client_workers >= MAX_CLIENT_WORKERS) {
            pthread_mutex_unlock(&client_workers_mutex);
            send_text(fd, "ERR busy\n");
            close(fd);
            continue;
        }
        client_workers++;
        pthread_mutex_unlock(&client_workers_mutex);
        ClientContext *ctx = calloc(1, sizeof(ClientContext));
        if (!ctx) {
            pthread_mutex_lock(&client_workers_mutex);
            client_workers--;
            pthread_mutex_unlock(&client_workers_mutex);
            close(fd);
            continue;
        }
        ctx->fd = fd;
        ctx->require_proxy = use_proxy;
        const char *client_ip = inet_ntop(AF_INET, &client_addr.sin_addr, ctx->ip, sizeof(ctx->ip));
        if (!client_ip) snprintf(ctx->ip, sizeof(ctx->ip), "0.0.0.0");
        pthread_t tid;
        if (pthread_create(&tid, NULL, handle_client, ctx) == 0) {
            pthread_detach(tid);
        } else {
            free(ctx);
            pthread_mutex_lock(&client_workers_mutex);
            client_workers--;
            pthread_mutex_unlock(&client_workers_mutex);
            close(fd);
        }
    }
}
