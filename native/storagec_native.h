#ifndef STORAGEC_NATIVE_H
#define STORAGEC_NATIVE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(STORAGEC_NATIVE_EXPORTS)
#    define SG_API __declspec(dllexport)
#  else
#    define SG_API __declspec(dllimport)
#  endif
#else
#  define SG_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

SG_API int sg_abi_version(void);
SG_API int sg_client_create(const char *server, const char *share, const char *user,
    const char *password, const char *domain, int port, int timeout_seconds,
    int require_signing, int require_encryption, char *error, size_t error_capacity,
    void **client_out);
SG_API void sg_client_destroy(void *client);
SG_API int sg_open_read(void *client, const char *path, char *error, size_t error_capacity,
    void **file_out, uint64_t *file_size_out);
SG_API int sg_open_write(void *client, const char *path, int overwrite,
    char *error, size_t error_capacity, void **file_out);
SG_API int sg_file_read(void *client, void *file, uint8_t *buffer, uint32_t count,
    char *error, size_t error_capacity, uint32_t *bytes_read_out);
SG_API int sg_file_write(void *client, void *file, const uint8_t *buffer, uint32_t count,
    char *error, size_t error_capacity, uint32_t *bytes_written_out);
SG_API int sg_file_flush(void *client, void *file, char *error, size_t error_capacity);
SG_API int sg_file_close(void *client, void *file, char *error, size_t error_capacity);

#ifdef __cplusplus
}
#endif
#endif
