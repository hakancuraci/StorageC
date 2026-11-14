#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

#if defined(_WIN32)
#define API __declspec(dllexport)
#define MKDIR(path) _mkdir(path)
#include <direct.h>
#else
#define API __attribute__((visibility("default")))
#define MKDIR(path) mkdir(path, 0777)
#endif

struct client { char *root; };
struct file { FILE *stream; };
static int live_clients;
static int live_files;

static char *duplicate(const char *value) {
    size_t length = strlen(value) + 1;
    char *copy = (char *)malloc(length);
    if (copy != NULL) memcpy(copy, value, length);
    return copy;
}

static void message(char *error, size_t capacity, const char *text) {
    if (error != NULL && capacity > 0) { snprintf(error, capacity, "%s: %s", text, strerror(errno)); error[capacity - 1] = 0; }
}

static char *full_path(struct client *client, const char *path) {
    size_t root_length = strlen(client->root);
    size_t path_length = strlen(path);
    char *result = (char *)malloc(root_length + path_length + 2);
    if (result == NULL) return NULL;
    memcpy(result, client->root, root_length);
    result[root_length] = '/';
    for (size_t i = 0; i < path_length; i++) result[root_length + 1 + i] = path[i] == '\\' ? '/' : path[i];
    result[root_length + path_length + 1] = 0;
    return result;
}

static void ensure_parent(char *path) {
    for (char *cursor = path + 1; *cursor != 0; cursor++) {
        if (*cursor != '/') continue;
        char saved = *cursor; *cursor = 0; (void)MKDIR(path); *cursor = saved;
    }
}

API int sg_abi_version(void) { return 1; }
API int sg_client_create(const char *server, const char *share, const char *user, const char *password,
    const char *domain, int port, int timeout_seconds, int sign, int seal, char *error, size_t capacity, void **out) {
    (void)server; (void)user; (void)password; (void)domain; (void)port; (void)timeout_seconds; (void)sign; (void)seal; (void)error; (void)capacity;
    struct client *value = (struct client *)calloc(1, sizeof(*value));
    if (value == NULL) return -ENOMEM;
    value->root = duplicate(share);
    if (value->root == NULL) { free(value); return -ENOMEM; }
    *out = value; live_clients++; return 0;
}
API void sg_client_destroy(void *opaque) { struct client *value = opaque; if (value == NULL) return; free(value->root); free(value); live_clients--; }
API int sg_open_read(void *client_ptr, const char *path, char *error, size_t capacity, void **out, uint64_t *size) {
    struct client *client = client_ptr; char *full = full_path(client, path); if (full == NULL) return -ENOMEM;
    FILE *stream = fopen(full, "rb"); free(full); if (stream == NULL) { message(error, capacity, "open read"); return -ENOENT; }
    if (fseek(stream, 0, SEEK_END) != 0) { fclose(stream); return -EIO; }
    long length = ftell(stream); if (length < 0 || fseek(stream, 0, SEEK_SET) != 0) { fclose(stream); return -EIO; }
    struct file *file = calloc(1, sizeof(*file)); if (file == NULL) { fclose(stream); return -ENOMEM; }
    file->stream = stream; *out = file; *size = (uint64_t)length; live_files++; return 0;
}
API int sg_open_write(void *client_ptr, const char *path, int overwrite, char *error, size_t capacity, void **out) {
    struct client *client = client_ptr; char *full = full_path(client, path); if (full == NULL) return -ENOMEM; ensure_parent(full);
    if (!overwrite) { FILE *existing = fopen(full, "rb"); if (existing != NULL) { fclose(existing); free(full); errno = EEXIST; message(error, capacity, "exists"); return -EEXIST; } }
    FILE *stream = fopen(full, "wb"); free(full); if (stream == NULL) { message(error, capacity, "open write"); return -EIO; }
    struct file *file = calloc(1, sizeof(*file)); if (file == NULL) { fclose(stream); return -ENOMEM; }
    file->stream = stream; *out = file; live_files++; return 0;
}
API int sg_file_read(void *client, void *file_ptr, uint8_t *buffer, uint32_t count, char *error, size_t capacity, uint32_t *read_out) {
    (void)client; struct file *file = file_ptr; size_t read = fread(buffer, 1, count, file->stream); if (read == 0 && ferror(file->stream)) { message(error, capacity, "read"); return -EIO; } *read_out = (uint32_t)read; return 0;
}
API int sg_file_write(void *client, void *file_ptr, const uint8_t *buffer, uint32_t count, char *error, size_t capacity, uint32_t *written_out) {
    (void)client; struct file *file = file_ptr; uint32_t limited = count > 65537 ? 65537 : count; size_t written = fwrite(buffer, 1, limited, file->stream); if (written == 0) { message(error, capacity, "write"); return -EIO; } *written_out = (uint32_t)written; return 0;
}
API int sg_file_flush(void *client, void *file_ptr, char *error, size_t capacity) { (void)client; struct file *file = file_ptr; if (fflush(file->stream) != 0) { message(error, capacity, "flush"); return -EIO; } return 0; }
API int sg_file_close(void *client, void *file_ptr, char *error, size_t capacity) { (void)client; struct file *file = file_ptr; if (file == NULL) return 0; int status = fclose(file->stream); free(file); live_files--; if (status != 0) { message(error, capacity, "close"); return -EIO; } return 0; }
API int sg_test_live_clients(void) { return live_clients; }
API int sg_test_live_files(void) { return live_files; }
