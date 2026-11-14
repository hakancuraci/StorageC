#include "storagec_native.h"

#if defined(_WIN32)
#include <winsock2.h>
#else
#include <sys/select.h>
#endif
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <smb2/smb2.h>
#include <smb2/libsmb2.h>

struct sg_client { struct smb2_context *context; };
struct sg_file { struct smb2fh *handle; };

static void set_error(char *destination, size_t capacity, const char *operation,
                      struct smb2_context *context, int status)
{
    if (destination == NULL || capacity == 0) return;
    const char *detail = context == NULL ? NULL : smb2_get_error(context);
    if (detail == NULL || detail[0] == '\0') detail = "no native detail";
    (void)snprintf(destination, capacity, "%s failed (%d): %s", operation, status, detail);
    destination[capacity - 1] = '\0';
}

static int invalid_argument(char *error, size_t capacity, const char *operation)
{
    set_error(error, capacity, operation, NULL, -EINVAL);
    return -EINVAL;
}

SG_API int sg_abi_version(void) { return 1; }

SG_API int sg_client_create(const char *server, const char *share, const char *user,
    const char *password, const char *domain, int port, int timeout_seconds,
    int require_signing, int require_encryption, char *error, size_t error_capacity,
    void **client_out)
{
    (void)port; /* The managed layer encodes non-default ports in server. */
    if (client_out == NULL || server == NULL || share == NULL || user == NULL || password == NULL)
        return invalid_argument(error, error_capacity, "sg_client_create");
    *client_out = NULL;

    struct sg_client *client = (struct sg_client *)calloc(1, sizeof(*client));
    if (client == NULL) return -ENOMEM;
    client->context = smb2_init_context();
    if (client->context == NULL) { free(client); return -ENOMEM; }

    smb2_set_authentication(client->context, SMB2_SEC_NTLMSSP);
    smb2_set_user(client->context, user);
    smb2_set_password(client->context, password);
    if (domain != NULL && domain[0] != '\0') smb2_set_domain(client->context, domain);
    smb2_set_timeout(client->context, timeout_seconds);
    smb2_set_sign(client->context, require_signing);
    smb2_set_seal(client->context, require_encryption);

    int status = smb2_connect_share(client->context, server, share, user);
    if (status != 0) {
        set_error(error, error_capacity, "smb2_connect_share", client->context, status);
        smb2_destroy_context(client->context);
        free(client);
        return status;
    }
    *client_out = client;
    return 0;
}

SG_API void sg_client_destroy(void *opaque)
{
    struct sg_client *client = (struct sg_client *)opaque;
    if (client == NULL) return;
    if (client->context != NULL) {
        (void)smb2_disconnect_share(client->context);
        smb2_destroy_context(client->context);
        client->context = NULL;
    }
    free(client);
}

SG_API int sg_open_read(void *client_opaque, const char *path, char *error, size_t error_capacity,
    void **file_out, uint64_t *file_size_out)
{
    struct sg_client *client = (struct sg_client *)client_opaque;
    if (client == NULL || path == NULL || file_out == NULL || file_size_out == NULL)
        return invalid_argument(error, error_capacity, "sg_open_read");
    *file_out = NULL;
    *file_size_out = 0;
    struct smb2fh *handle = smb2_open(client->context, path, O_RDONLY);
    if (handle == NULL) { set_error(error, error_capacity, "smb2_open", client->context, -EIO); return -EIO; }
    struct smb2_stat_64 stat_value;
    int status = smb2_fstat(client->context, handle, &stat_value);
    if (status != 0) {
        set_error(error, error_capacity, "smb2_fstat", client->context, status);
        (void)smb2_close(client->context, handle);
        return status;
    }
    struct sg_file *file = (struct sg_file *)calloc(1, sizeof(*file));
    if (file == NULL) { (void)smb2_close(client->context, handle); return -ENOMEM; }
    file->handle = handle;
    *file_size_out = stat_value.smb2_size;
    *file_out = file;
    return 0;
}

SG_API int sg_open_write(void *client_opaque, const char *path, int overwrite,
    char *error, size_t error_capacity, void **file_out)
{
    struct sg_client *client = (struct sg_client *)client_opaque;
    if (client == NULL || path == NULL || file_out == NULL)
        return invalid_argument(error, error_capacity, "sg_open_write");
    *file_out = NULL;
    int flags = O_WRONLY | O_CREAT | (overwrite ? 0 : O_EXCL);
    struct smb2fh *handle = smb2_open(client->context, path, flags);
    if (handle == NULL) { set_error(error, error_capacity, "smb2_open", client->context, -EIO); return -EIO; }
    if (overwrite) {
        int status = smb2_ftruncate(client->context, handle, 0);
        if (status != 0) {
            set_error(error, error_capacity, "smb2_ftruncate", client->context, status);
            (void)smb2_close(client->context, handle);
            return status;
        }
    }
    struct sg_file *file = (struct sg_file *)calloc(1, sizeof(*file));
    if (file == NULL) { (void)smb2_close(client->context, handle); return -ENOMEM; }
    file->handle = handle;
    *file_out = file;
    return 0;
}

SG_API int sg_file_read(void *client_opaque, void *file_opaque, uint8_t *buffer, uint32_t count,
    char *error, size_t error_capacity, uint32_t *bytes_read_out)
{
    struct sg_client *client = (struct sg_client *)client_opaque;
    struct sg_file *file = (struct sg_file *)file_opaque;
    if (client == NULL || file == NULL || buffer == NULL || bytes_read_out == NULL)
        return invalid_argument(error, error_capacity, "sg_file_read");
    *bytes_read_out = 0;
    uint32_t maximum = smb2_get_max_read_size(client->context);
    if (maximum > 0 && count > maximum) count = maximum;
    int status = smb2_read(client->context, file->handle, buffer, count);
    if (status < 0) { set_error(error, error_capacity, "smb2_read", client->context, status); return status; }
    *bytes_read_out = (uint32_t)status;
    return 0;
}

SG_API int sg_file_write(void *client_opaque, void *file_opaque, const uint8_t *buffer, uint32_t count,
    char *error, size_t error_capacity, uint32_t *bytes_written_out)
{
    struct sg_client *client = (struct sg_client *)client_opaque;
    struct sg_file *file = (struct sg_file *)file_opaque;
    if (client == NULL || file == NULL || buffer == NULL || bytes_written_out == NULL)
        return invalid_argument(error, error_capacity, "sg_file_write");
    *bytes_written_out = 0;
    uint32_t maximum = smb2_get_max_write_size(client->context);
    if (maximum > 0 && count > maximum) count = maximum;
    int status = smb2_write(client->context, file->handle, buffer, count);
    if (status < 0) { set_error(error, error_capacity, "smb2_write", client->context, status); return status; }
    *bytes_written_out = (uint32_t)status;
    return 0;
}

SG_API int sg_file_flush(void *client_opaque, void *file_opaque, char *error, size_t error_capacity)
{
    struct sg_client *client = (struct sg_client *)client_opaque;
    struct sg_file *file = (struct sg_file *)file_opaque;
    if (client == NULL || file == NULL) return invalid_argument(error, error_capacity, "sg_file_flush");
    int status = smb2_fsync(client->context, file->handle);
    if (status != 0) set_error(error, error_capacity, "smb2_fsync", client->context, status);
    return status;
}

SG_API int sg_file_close(void *client_opaque, void *file_opaque, char *error, size_t error_capacity)
{
    struct sg_client *client = (struct sg_client *)client_opaque;
    struct sg_file *file = (struct sg_file *)file_opaque;
    if (file == NULL) return 0;
    if (client == NULL) { free(file); return invalid_argument(error, error_capacity, "sg_file_close"); }
    int status = smb2_close(client->context, file->handle);
    if (status != 0) set_error(error, error_capacity, "smb2_close", client->context, status);
    file->handle = NULL;
    free(file);
    return status;
}
