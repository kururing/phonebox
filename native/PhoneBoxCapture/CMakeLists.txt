cmake_minimum_required(VERSION 3.20)
project(PhoneBoxCapture LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(PhoneBoxCapture SHARED
    src/d3d_duplicator.cpp
    src/nvenc_encoder.cpp
    src/api.cpp
)

target_include_directories(PhoneBoxCapture
    PUBLIC  include
    PRIVATE src third_party
)

target_compile_definitions(PhoneBoxCapture PRIVATE
    PB_CAP_EXPORTS
    UNICODE
    _UNICODE
    NOMINMAX
    WIN32_LEAN_AND_MEAN
)

target_link_libraries(PhoneBoxCapture PRIVATE
    d3d11
    dxgi
    d3dcompiler
    winmm
)

if(MSVC)
    target_compile_options(PhoneBoxCapture PRIVATE /W4 /O2 /EHsc /utf-8)
endif()
