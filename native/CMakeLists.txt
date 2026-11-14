cmake_minimum_required(VERSION 3.21)
project(storagec_native VERSION 0.1.0 LANGUAGES C)

include(FetchContent)
set(BUILD_SHARED_LIBS ON CACHE BOOL "" FORCE)
set(ENABLE_EXAMPLES OFF CACHE BOOL "" FORCE)
set(ENABLE_UTILS OFF CACHE BOOL "" FORCE)
set(ENABLE_LIBDCERPC OFF CACHE BOOL "" FORCE)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# Pin the exact upstream revision. Update deliberately and rerun all stress tests.
FetchContent_Declare(libsmb2
  GIT_REPOSITORY https://github.com/sahlberg/libsmb2.git
  GIT_TAG 177a9dec5545d16591076a524e0a68c8a7051749
  GIT_SHALLOW FALSE)
FetchContent_MakeAvailable(libsmb2)

add_library(storagec_native SHARED storagec_native.c)
target_compile_features(storagec_native PRIVATE c_std_11)
target_include_directories(storagec_native PRIVATE ${libsmb2_SOURCE_DIR}/include)
target_link_libraries(storagec_native PRIVATE smb2)
target_compile_definitions(storagec_native PRIVATE STORAGEC_NATIVE_EXPORTS)
set_target_properties(storagec_native PROPERTIES
  BUILD_RPATH "$ORIGIN"
  INSTALL_RPATH "$ORIGIN")

if(MSVC)
  target_compile_options(storagec_native PRIVATE /W4 /WX)
else()
  target_compile_options(storagec_native PRIVATE -Wall -Wextra -Werror)
endif()

install(TARGETS storagec_native smb2
  RUNTIME DESTINATION .
  LIBRARY DESTINATION .
  ARCHIVE DESTINATION lib)
