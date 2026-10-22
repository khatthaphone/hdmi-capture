// vcam: creates/removes the HDMI Captain test camera (MFCreateVirtualCamera), reports
// registration, lists cameras, grabs frames from the virtual camera, and self-tests the
// pattern generator and the media source in-process.
//
//   vcam status                       is the media source registered in HKLM?
//   vcam add [--seconds N] [--grab F] create the camera (session lifetime) until N s pass or Enter
//   vcam list                         list video capture devices
//   vcam grab [--frames F] [--format nv12|yuy2] [--size WxH]
//                                     read frames from the virtual camera and check them
//   vcam selftest [--dll PATH]        pattern checks + in-process media source checks
#include "../src/Common.h"
#include "../src/Pattern.h"

#include <mfreadwrite.h>
#include <propvarutil.h>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <iostream>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
using namespace vcam;

namespace
{
    const std::wstring ClsidKey = std::wstring(L"Software\\Classes\\CLSID\\") + CLSID_HDMICaptainVCamString;

    std::wstring HrMessage(HRESULT hr)
    {
        wchar_t* text = nullptr;
        DWORD n = FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
            nullptr, static_cast<DWORD>(hr), 0, reinterpret_cast<LPWSTR>(&text), 0, nullptr);
        if (n == 0)
        {
            if (HMODULE mf = LoadLibraryExW(L"mferror.dll", nullptr, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_SEARCH_SYSTEM32))
            {
                n = FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_HMODULE | FORMAT_MESSAGE_IGNORE_INSERTS,
                    mf, static_cast<DWORD>(hr), 0, reinterpret_cast<LPWSTR>(&text), 0, nullptr);
                FreeLibrary(mf);
            }
        }
        std::wstring message = n ? std::wstring(text, n) : L"(no system message)";
        if (text)
            LocalFree(text);
        while (!message.empty() && (message.back() == L'\n' || message.back() == L'\r' || message.back() == L' '))
            message.pop_back();
        return message;
    }

    void PrintHr(const wchar_t* what, HRESULT hr)
    {
        std::wprintf(L"%ls failed: 0x%08X %ls\n", what, static_cast<unsigned>(hr), HrMessage(hr).c_str());
    }

    std::wstring ExeDir()
    {
        wchar_t path[MAX_PATH * 4];
        const DWORD n = GetModuleFileNameW(nullptr, path, static_cast<DWORD>(std::size(path)));
        std::wstring dir(path, n);
        const size_t slash = dir.find_last_of(L"\\/");
        return slash == std::wstring::npos ? L"." : dir.substr(0, slash);
    }

    std::wstring DefaultDllPath() { return ExeDir() + L"\\HDMICaptainVCam.dll"; }

    // Default value of HKLM\Software\Classes\CLSID\{clsid}\InprocServer32 (64-bit view), or empty.
    std::wstring RegisteredPath(HKEY root, LSTATUS* status)
    {
        wchar_t value[MAX_PATH * 4] = {};
        DWORD size = sizeof(value);
        *status = RegGetValueW(root, (ClsidKey + L"\\InprocServer32").c_str(), nullptr, RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_SUBKEY_WOW6464KEY,
            nullptr, value, &size);
        return *status == ERROR_SUCCESS ? std::wstring(value) : std::wstring();
    }

    bool IsRegistered(bool print)
    {
        LSTATUS status = ERROR_SUCCESS;
        const std::wstring path = RegisteredPath(HKEY_LOCAL_MACHINE, &status);
        if (path.empty())
        {
            if (print)
            {
                std::wprintf(L"not registered: %ls is not in HKLM\\%ls (0x%08X)\n", CLSID_HDMICaptainVCamString, ClsidKey.c_str(),
                    static_cast<unsigned>(HRESULT_FROM_WIN32(status)));
                std::wprintf(L"  run `regsvr32 \"%ls\"` from an elevated prompt (Frame Server only sees HKLM)\n", DefaultDllPath().c_str());
                LSTATUS userStatus = ERROR_SUCCESS;
                if (!RegisteredPath(HKEY_CURRENT_USER, &userStatus).empty())
                    std::wprintf(L"  note: an HKCU registration exists, but Frame Server (LocalService) cannot see it\n");
            }
            return false;
        }
        if (print)
        {
            const bool exists = GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
            std::wprintf(L"registered: %ls -> %ls%ls\n", CLSID_HDMICaptainVCamString, path.c_str(), exists ? L"" : L" (FILE MISSING)");
            if (_wcsicmp(path.c_str(), DefaultDllPath().c_str()) != 0)
                std::wprintf(L"  note: this is not the DLL beside vcam.exe (%ls)\n", DefaultDllPath().c_str());
        }
        return true;
    }

    struct ComScope
    {
        HRESULT hr;
        bool mf = false;
        ComScope()
        {
            hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            if (SUCCEEDED(hr))
            {
                hr = MFStartup(MF_VERSION);
                mf = SUCCEEDED(hr);
            }
        }
        ~ComScope()
        {
            if (mf)
                MFShutdown();
            CoUninitialize();
        }
        ComScope(const ComScope&) = delete;
        ComScope& operator=(const ComScope&) = delete;
    };

    // Locks a sample's buffer for reading as a vcam::Frame.
    class ReadLock
    {
    public:
        ReadLock(IMFSample* sample, PixelFormat format, uint32_t width, uint32_t height)
        {
            hr_ = sample->GetBufferByIndex(0, &buffer_);
            if (FAILED(hr_))
                return;
            BYTE* scan0 = nullptr;
            LONG pitch = 0;
            if (SUCCEEDED(buffer_.As(&buffer2d_)) && SUCCEEDED(hr_ = buffer2d_->Lock2D(&scan0, &pitch)))
            {
                frame_ = { format, width, height, scan0, pitch };
                return;
            }
            buffer2d_.Reset();
            DWORD length = 0;
            hr_ = buffer_->Lock(&scan0, nullptr, &length);
            if (FAILED(hr_))
                return;
            locked_ = true;
            if (length < FrameBytes(format, width, height))
                hr_ = E_NOT_SUFFICIENT_BUFFER;
            frame_ = { format, width, height, scan0, PackedPitch(format, width) };
        }
        ~ReadLock()
        {
            if (buffer2d_)
                buffer2d_->Unlock2D();
            else if (locked_)
                buffer_->Unlock();
        }
        ReadLock(const ReadLock&) = delete;
        ReadLock& operator=(const ReadLock&) = delete;
        HRESULT Result() const { return hr_; }
        const Frame& Get() const { return frame_; }

    private:
        ComPtr<IMFMediaBuffer> buffer_;
        ComPtr<IMF2DBuffer> buffer2d_;
        bool locked_ = false;
        HRESULT hr_ = E_FAIL;
        Frame frame_{};
    };

    bool ParseFormat(const GUID& subtype, PixelFormat* format)
    {
        if (subtype == MFVideoFormat_NV12)
            *format = PixelFormat::NV12;
        else if (subtype == MFVideoFormat_YUY2)
            *format = PixelFormat::YUY2;
        else
            return false;
        return true;
    }

    const wchar_t* FormatName(PixelFormat f) { return f == PixelFormat::NV12 ? L"NV12" : L"YUY2"; }

    // ---- list / grab ----------------------------------------------------------------------

    HRESULT EnumCameras(std::vector<ComPtr<IMFActivate>>* out)
    {
        ComPtr<IMFAttributes> attrs;
        VCAM_RETURN_IF_FAILED(MFCreateAttributes(&attrs, 1));
        VCAM_RETURN_IF_FAILED(attrs->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID));
        IMFActivate** devices = nullptr;
        UINT32 count = 0;
        VCAM_RETURN_IF_FAILED(MFEnumDeviceSources(attrs.Get(), &devices, &count));
        for (UINT32 i = 0; i < count; i++)
        {
            out->emplace_back(devices[i]);
            devices[i]->Release();
        }
        CoTaskMemFree(devices);
        return S_OK;
    }

    std::wstring GetString(IMFAttributes* a, REFGUID key)
    {
        wchar_t* value = nullptr;
        UINT32 length = 0;
        if (FAILED(a->GetAllocatedString(key, &value, &length)))
            return {};
        std::wstring s(value, length);
        CoTaskMemFree(value);
        return s;
    }

    int CmdList()
    {
        ComScope com;
        if (FAILED(com.hr))
            return PrintHr(L"MFStartup", com.hr), 1;
        std::vector<ComPtr<IMFActivate>> cameras;
        const HRESULT hr = EnumCameras(&cameras);
        if (FAILED(hr))
            return PrintHr(L"MFEnumDeviceSources", hr), 1;
        std::wprintf(L"%zu video capture device(s)\n", cameras.size());
        for (auto& c : cameras)
        {
            std::wprintf(L"  %ls\n    %ls\n", GetString(c.Get(), MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME).c_str(),
                GetString(c.Get(), MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK).c_str());
        }
        return 0;
    }

    struct GrabOptions
    {
        int frames = 120;
        GUID subtype = MFVideoFormat_NV12;
        UINT32 width = 1920;
        UINT32 height = 1080;
    };

    int Grab(const GrabOptions& options)
    {
        std::vector<ComPtr<IMFActivate>> cameras;
        HRESULT hr = EnumCameras(&cameras);
        if (FAILED(hr))
            return PrintHr(L"MFEnumDeviceSources", hr), 1;
        ComPtr<IMFActivate> camera;
        for (auto& c : cameras)
        {
            if (GetString(c.Get(), MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME) == FriendlyName)
                camera = c;
        }
        if (!camera)
        {
            std::wprintf(L"grab: no camera named \"%ls\" (is `vcam add` running?)\n", FriendlyName);
            return 1;
        }
        ComPtr<IMFMediaSource> source;
        hr = camera->ActivateObject(IID_PPV_ARGS(&source));
        if (FAILED(hr))
            return PrintHr(L"ActivateObject", hr), 1;
        ComPtr<IMFSourceReader> reader;
        hr = MFCreateSourceReaderFromMediaSource(source.Get(), nullptr, &reader);
        if (FAILED(hr))
            return source->Shutdown(), PrintHr(L"MFCreateSourceReaderFromMediaSource", hr), 1;

        const DWORD stream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
        ComPtr<IMFMediaType> chosen;
        std::wprintf(L"native types:\n");
        for (DWORD i = 0;; i++)
        {
            ComPtr<IMFMediaType> type;
            if (FAILED(reader->GetNativeMediaType(stream, i, &type)))
                break;
            GUID subtype{};
            UINT32 w = 0, h = 0, num = 0, den = 0;
            type->GetGUID(MF_MT_SUBTYPE, &subtype);
            MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h);
            MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &num, &den);
            PixelFormat f{};
            std::wprintf(L"  %ls %ux%u @ %u/%u\n", ParseFormat(subtype, &f) ? FormatName(f) : L"other", w, h, num, den);
            if (!chosen && subtype == options.subtype && w == options.width && h == options.height)
                chosen = type;
        }
        if (!chosen)
        {
            std::wprintf(L"grab: requested type not offered\n");
            source->Shutdown();
            return 1;
        }
        hr = reader->SetCurrentMediaType(stream, nullptr, chosen.Get());
        if (FAILED(hr))
            return source->Shutdown(), PrintHr(L"SetCurrentMediaType", hr), 1;

        PixelFormat format{};
        ParseFormat(options.subtype, &format);
        int failures = 0, gaps = 0, got = 0;
        int64_t firstCounter = -1, lastCounter = -1;
        LONGLONG firstTime = 0, lastTime = 0;
        const auto start = std::chrono::steady_clock::now();
        while (got < options.frames)
        {
            DWORD flags = 0;
            LONGLONG time = 0;
            ComPtr<IMFSample> sample;
            hr = reader->ReadSample(stream, 0, nullptr, &flags, &time, &sample);
            if (FAILED(hr))
                return source->Shutdown(), PrintHr(L"ReadSample", hr), 1;
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM)
                break;
            if (!sample)
                continue;
            ReadLock lock(sample.Get(), format, options.width, options.height);
            if (FAILED(lock.Result()))
                return source->Shutdown(), PrintHr(L"buffer lock", lock.Result()), 1;
            const int64_t counter = DecodeCounter(lock.Get());
            if (got == 0)
            {
                std::string report;
                failures = CheckBars(lock.Get(), 2, &report);
                if (failures)
                    std::printf("bar check: %d mismatch(es)\n%s", failures, report.c_str());
                firstCounter = counter;
                firstTime = time;
            }
            else if (counter >= 0 && lastCounter >= 0 && counter != lastCounter + 1)
            {
                gaps++;
            }
            lastCounter = counter;
            lastTime = time;
            got++;
        }
        const double wall = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
        source->Shutdown();
        const double span = (lastTime - firstTime) / 1e7;
        std::wprintf(L"grabbed %d %ls %ux%u frames in %.2f s; timestamps span %.3f s (%.2f fps); counter %lld -> %lld, %d gap(s); bars %ls\n",
            got, FormatName(format), options.width, options.height, wall, span, got > 1 && span > 0 ? (got - 1) / span : 0.0,
            static_cast<long long>(firstCounter), static_cast<long long>(lastCounter), gaps, failures ? L"FAILED" : L"ok");
        return failures || got == 0 || firstCounter < 0 ? 1 : 0;
    }

    bool ParseGrabOption(int argc, wchar_t** argv, int* i, GrabOptions* options)
    {
        const std::wstring arg = argv[*i];
        if (*i + 1 >= argc)
            return false;
        const wchar_t* value = argv[++*i];
        if (arg == L"--frames")
            options->frames = std::max(1, _wtoi(value));
        else if (arg == L"--format")
        {
            if (_wcsicmp(value, L"nv12") == 0)
                options->subtype = MFVideoFormat_NV12;
            else if (_wcsicmp(value, L"yuy2") == 0)
                options->subtype = MFVideoFormat_YUY2;
            else
                return false;
        }
        else if (arg == L"--size")
        {
            unsigned w = 0, h = 0;
            if (swscanf_s(value, L"%ux%u", &w, &h) != 2)
                return false;
            options->width = w;
            options->height = h;
        }
        else
            return false;
        return true;
    }

    int CmdGrab(int argc, wchar_t** argv)
    {
        GrabOptions options;
        for (int i = 2; i < argc; i++)
        {
            if (!ParseGrabOption(argc, argv, &i, &options))
                return std::wprintf(L"usage: vcam grab [--frames N] [--format nv12|yuy2] [--size WxH]\n"), 2;
        }
        ComScope com;
        if (FAILED(com.hr))
            return PrintHr(L"MFStartup", com.hr), 1;
        return Grab(options);
    }

    // ---- add ------------------------------------------------------------------------------

    int CmdAdd(int argc, wchar_t** argv)
    {
        int seconds = -1;
        int grabFrames = 0;
        for (int i = 2; i < argc; i++)
        {
            const std::wstring arg = argv[i];
            if (arg == L"--seconds" && i + 1 < argc)
                seconds = std::max(0, _wtoi(argv[++i]));
            else if (arg == L"--grab" && i + 1 < argc)
                grabFrames = std::max(1, _wtoi(argv[++i]));
            else
                return std::wprintf(L"usage: vcam add [--seconds N] [--grab FRAMES]\n"), 2;
        }

        const bool registered = IsRegistered(false);
        ComScope com;
        if (FAILED(com.hr))
            return PrintHr(L"MFStartup", com.hr), 1;

        // Never on a UI thread: the consent prompt would deadlock it. This is a console MTA thread.
        ComPtr<IMFVirtualCamera> camera;
        HRESULT hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_Session,
            MFVirtualCameraAccess_CurrentUser, FriendlyName, CLSID_HDMICaptainVCamString, nullptr, 0, &camera);
        if (FAILED(hr))
        {
            PrintHr(L"MFCreateVirtualCamera", hr);
            if (!registered)
                IsRegistered(true);
            return 1;
        }
        hr = camera->Start(nullptr);
        if (FAILED(hr))
        {
            PrintHr(L"IMFVirtualCamera::Start", hr);
            if (!registered)
                IsRegistered(true);
            camera->Remove();
            return 1;
        }
        std::wprintf(L"\"%ls\" is running (session lifetime, current user).\n", FriendlyName);

        int result = 0;
        if (grabFrames > 0)
        {
            GrabOptions options;
            options.frames = grabFrames;
            result = Grab(options);
        }
        if (seconds != 0 && (grabFrames == 0 || seconds > 0))
        {
            HANDLE stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            std::thread([stop] {
                std::wstring line;
                if (std::getline(std::wcin, line)) // EOF (no console, e.g. a script) waits for --seconds instead
                    SetEvent(stop);
            }).detach();
            if (seconds > 0)
                std::wprintf(L"Removing in %d s (or press Enter).\n", seconds);
            else
                std::wprintf(L"Press Enter to remove it.\n");
            WaitForSingleObject(stop, seconds > 0 ? static_cast<DWORD>(seconds) * 1000 : INFINITE);
            CloseHandle(stop);
        }

        // Remove only (no Shutdown first): a second Shutdown of the source blocks removal.
        hr = camera->Remove();
        if (FAILED(hr))
            return PrintHr(L"IMFVirtualCamera::Remove", hr), 1;
        std::wprintf(L"removed\n");
        return result;
    }

    // ---- selftest -------------------------------------------------------------------------

    int g_failures = 0;

    void Check(bool ok, const char* what)
    {
        std::printf("  [%s] %s\n", ok ? "ok" : "FAIL", what);
        if (!ok)
            g_failures++;
    }

    void PatternTests()
    {
        std::printf("pattern generator\n");
        const Ycc white = Bt709Limited(0.75, 0.75, 0.75);
        const Ycc yellow = Bt709Limited(0.75, 0.75, 0);
        const Ycc blue = Bt709Limited(0, 0, 0.75);
        Check(white.y == 180 && white.cb == 128 && white.cr == 128, "Bt709Limited 75% white = 180/128/128");
        Check(yellow.y == 168 && yellow.cb == 44 && yellow.cr == 136, "Bt709Limited 75% yellow = 168/44/136");
        Check(blue.y == 28 && blue.cb == 212 && blue.cr == 120, "Bt709Limited 75% blue = 28/212/120");

        const PixelFormat formats[] = { PixelFormat::NV12, PixelFormat::YUY2 };
        const uint32_t sizes[][2] = { { 1280, 720 }, { 1920, 1080 } };
        for (PixelFormat f : formats)
        {
            for (const auto& s : sizes)
            {
                PatternCache cache;
                cache.Prepare(f, s[0], s[1]);
                // Padded pitch, like a real MF 2D buffer, to catch pitch mistakes.
                const ptrdiff_t pitch = PackedPitch(f, s[0]) + 64;
                const size_t rows = f == PixelFormat::NV12 ? s[1] + s[1] / 2 : s[1];
                std::vector<uint8_t> buffer(static_cast<size_t>(pitch) * rows, 0xAB);
                const Frame frame{ f, s[0], s[1], buffer.data(), pitch };
                char label[96];
                for (uint64_t n : { 0ull, 7ull, 12345678ull, 99999999ull, 100000123ull })
                {
                    cache.Render(frame, n);
                    std::snprintf(label, sizeof label, "%ls %ux%u counter %llu reads back", FormatName(f), s[0], s[1],
                        static_cast<unsigned long long>(n));
                    Check(DecodeCounter(frame) == static_cast<int64_t>(n % 100000000), label);
                }
                std::string report;
                const int failures = CheckBars(frame, 0, &report);
                std::snprintf(label, sizeof label, "%ls %ux%u bars match BT.709 75%% reference exactly", FormatName(f), s[0], s[1]);
                Check(failures == 0, label);
                if (failures)
                    std::printf("%s", report.c_str());
            }
        }
    }

    // Waits for an event of `type` (other events are skipped), up to `timeoutMs`.
    HRESULT WaitEvent(IMFMediaEventGenerator* generator, MediaEventType type, DWORD timeoutMs, IMFMediaEvent** out)
    {
        const ULONGLONG deadline = GetTickCount64() + timeoutMs;
        for (;;)
        {
            ComPtr<IMFMediaEvent> event;
            const HRESULT hr = generator->GetEvent(MF_EVENT_FLAG_NO_WAIT, &event);
            if (hr == MF_E_NO_EVENTS_AVAILABLE)
            {
                if (GetTickCount64() > deadline)
                    return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
                Sleep(1);
                continue;
            }
            VCAM_RETURN_IF_FAILED(hr);
            MediaEventType got = MEUnknown;
            event->GetType(&got);
            HRESULT status = S_OK;
            event->GetStatus(&status);
            if (got == MEError || FAILED(status))
                return FAILED(status) ? status : E_FAIL;
            if (got == type)
            {
                if (out)
                    *out = event.Detach();
                return S_OK;
            }
        }
    }

    void SourceTests(const std::wstring& dllPath)
    {
        std::printf("in-process media source (%ls)\n", dllPath.c_str());
        HMODULE dll = LoadLibraryExW(dllPath.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        Check(dll != nullptr, "LoadLibrary");
        if (!dll)
            return;
        using GetClassObjectFn = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
        using CanUnloadFn = HRESULT(STDAPICALLTYPE*)();
        auto getClassObject = reinterpret_cast<GetClassObjectFn>(reinterpret_cast<void*>(GetProcAddress(dll, "DllGetClassObject")));
        auto canUnload = reinterpret_cast<CanUnloadFn>(reinterpret_cast<void*>(GetProcAddress(dll, "DllCanUnloadNow")));
        Check(getClassObject && canUnload && GetProcAddress(dll, "DllRegisterServer") && GetProcAddress(dll, "DllUnregisterServer"),
            "exports DllGetClassObject, DllCanUnloadNow, DllRegisterServer, DllUnregisterServer");
        if (!getClassObject || !canUnload)
            return;
        {
            ComPtr<IClassFactory> factory;
            Check(SUCCEEDED(getClassObject(CLSID_HDMICaptainVCam, IID_PPV_ARGS(&factory))), "DllGetClassObject(CLSID) -> IClassFactory");
            ComPtr<IClassFactory> other;
            Check(getClassObject(GUID_NULL, IID_PPV_ARGS(&other)) == CLASS_E_CLASSNOTAVAILABLE, "other CLSIDs -> CLASS_E_CLASSNOTAVAILABLE");
            ComPtr<IMFActivate> activate;
            Check(factory && SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&activate))), "IClassFactory -> IMFActivate");
            if (!activate)
                return;
            UINT32 associated = 0;
            Check(SUCCEEDED(activate->GetUINT32(MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES, &associated)) && associated == 1,
                "activator sets MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES");

            ComPtr<IMFMediaSource> source;
            Check(SUCCEEDED(activate->ActivateObject(IID_PPV_ARGS(&source))), "ActivateObject -> IMFMediaSource");
            if (!source)
                return;
            ComPtr<IMFMediaSourceEx> sourceEx;
            ComPtr<IMFGetService> service;
            ComPtr<IKsControl> ks;
            ComPtr<IMFSampleAllocatorControl> allocatorControl;
            ComPtr<IMFAttributes> sourceAttributes;
            Check(SUCCEEDED(source.As(&sourceEx)) && SUCCEEDED(source.As(&service)) && SUCCEEDED(source.As(&ks)) &&
                    SUCCEEDED(source.As(&allocatorControl)) && SUCCEEDED(source.As(&sourceAttributes)),
                "source exposes IMFMediaSourceEx, IMFGetService, IKsControl, IMFSampleAllocatorControl, IMFAttributes");
            if (ks)
            {
                KSPROPERTY property{};
                ULONG returned = 1;
                Check(ks->KsProperty(&property, sizeof property, nullptr, 0, &returned) == HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND) && returned == 0,
                    "IKsControl::KsProperty -> ERROR_SET_NOT_FOUND");
            }
            if (sourceEx)
            {
                ComPtr<IMFAttributes> attrs;
                ComPtr<IUnknown> profiles;
                Check(SUCCEEDED(sourceEx->GetSourceAttributes(&attrs)) &&
                        SUCCEEDED(attrs->GetUnknown(MF_DEVICEMFT_SENSORPROFILE_COLLECTION, IID_PPV_ARGS(&profiles))),
                    "source attributes carry a sensor profile collection");
                ComPtr<IMFAttributes> streamAttrs;
                GUID category{};
                Check(SUCCEEDED(sourceEx->GetStreamAttributes(0, &streamAttrs)) &&
                        SUCCEEDED(streamAttrs->GetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, &category)) && category == PINNAME_VIDEO_CAPTURE,
                    "stream 0 attributes: category PINNAME_VIDEO_CAPTURE");
            }
            DWORD characteristics = 0;
            Check(SUCCEEDED(source->GetCharacteristics(&characteristics)) && (characteristics & MFMEDIASOURCE_IS_LIVE), "source is live");

            ComPtr<IMFPresentationDescriptor> pd;
            Check(SUCCEEDED(source->CreatePresentationDescriptor(&pd)), "CreatePresentationDescriptor");
            DWORD streams = 0;
            if (pd)
                pd->GetStreamDescriptorCount(&streams);
            Check(streams == 1, "one stream");
            if (streams != 1)
                return;
            BOOL selected = FALSE;
            ComPtr<IMFStreamDescriptor> sd;
            ComPtr<IMFMediaTypeHandler> handler;
            pd->GetStreamDescriptorByIndex(0, &selected, &sd);
            sd->GetMediaTypeHandler(&handler);
            DWORD typeCount = 0;
            handler->GetMediaTypeCount(&typeCount);
            Check(typeCount == 4, "four media types (NV12/YUY2 x 1080p/720p)");

            for (DWORD t = 0; t < typeCount; t++)
            {
                ComPtr<IMFMediaType> type;
                handler->GetMediaTypeByIndex(t, &type);
                GUID subtype{};
                UINT32 w = 0, h = 0, num = 0, den = 0, range = 0, matrix = 0;
                type->GetGUID(MF_MT_SUBTYPE, &subtype);
                MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h);
                MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &num, &den);
                type->GetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, &range);
                type->GetUINT32(MF_MT_YUV_MATRIX, &matrix);
                PixelFormat format{};
                char label[160];
                const bool known = ParseFormat(subtype, &format);
                std::snprintf(label, sizeof label, "type %lu: %ls %ux%u @ %u/%u, BT.709 limited", t, known ? FormatName(format) : L"?", w, h, num, den);
                Check(known && num == 60 && den == 1 && range == MFNominalRange_16_235 && matrix == MFVideoTransferMatrix_BT709, label);
                if (!known)
                    continue;

                handler->SetCurrentMediaType(type.Get());
                pd->SelectStream(0);
                PROPVARIANT start;
                PropVariantInit(&start);
                HRESULT hr = source->Start(pd.Get(), nullptr, &start);
                ComPtr<IMFMediaEvent> newStream;
                if (SUCCEEDED(hr))
                    hr = WaitEvent(source.Get(), MENewStream, 1000, &newStream);
                if (SUCCEEDED(hr))
                    hr = WaitEvent(source.Get(), MESourceStarted, 1000, nullptr);
                ComPtr<IMFMediaStream> stream;
                if (SUCCEEDED(hr))
                {
                    PROPVARIANT value;
                    PropVariantInit(&value);
                    newStream->GetValue(&value);
                    if (value.vt == VT_UNKNOWN && value.punkVal)
                        value.punkVal->QueryInterface(IID_PPV_ARGS(&stream));
                    PropVariantClear(&value);
                }
                if (SUCCEEDED(hr) && !stream)
                    hr = E_NOINTERFACE;
                if (SUCCEEDED(hr))
                    hr = WaitEvent(stream.Get(), MEStreamStarted, 1000, nullptr);
                std::snprintf(label, sizeof label, "  Start -> MENewStream, MESourceStarted, MEStreamStarted (0x%08X)", static_cast<unsigned>(hr));
                Check(SUCCEEDED(hr), label);
                if (FAILED(hr))
                    continue;

                const int frames = 12;
                int good = 0, counterOk = 0, durationOk = 0;
                LONGLONG firstTime = 0, lastTime = 0;
                for (int i = 0; i < frames; i++)
                {
                    hr = stream->RequestSample(nullptr);
                    ComPtr<IMFMediaEvent> event;
                    if (SUCCEEDED(hr))
                        hr = WaitEvent(stream.Get(), MEMediaSample, 1000, &event);
                    if (FAILED(hr))
                        break;
                    PROPVARIANT value;
                    PropVariantInit(&value);
                    event->GetValue(&value);
                    ComPtr<IMFSample> sample;
                    if (value.vt == VT_UNKNOWN && value.punkVal)
                        value.punkVal->QueryInterface(IID_PPV_ARGS(&sample));
                    PropVariantClear(&value);
                    if (!sample)
                        break;
                    LONGLONG time = 0, duration = 0;
                    sample->GetSampleTime(&time);
                    sample->GetSampleDuration(&duration);
                    durationOk += duration == FrameDuration;
                    if (i == 0)
                        firstTime = time;
                    lastTime = time;
                    ReadLock lock(sample.Get(), format, w, h);
                    if (SUCCEEDED(lock.Result()))
                    {
                        good += CheckBars(lock.Get(), 0, nullptr) == 0;
                        counterOk += DecodeCounter(lock.Get()) == i;
                    }
                }
                std::snprintf(label, sizeof label, "  %d/%d samples: bars %d ok, counter %d ok, duration %d ok", frames, frames, good, counterOk, durationOk);
                Check(good == frames && counterOk == frames && durationOk == frames, label);
                const double fps = lastTime > firstTime ? (frames - 1) * 1e7 / static_cast<double>(lastTime - firstTime) : 0;
                std::snprintf(label, sizeof label, "  sample times paced at %.1f fps (expect ~60)", fps);
                Check(fps > 50 && fps < 70, label);

                hr = source->Stop();
                if (SUCCEEDED(hr))
                    hr = WaitEvent(source.Get(), MESourceStopped, 1000, nullptr);
                Check(SUCCEEDED(hr), "  Stop -> MESourceStopped");
            }
            Check(SUCCEEDED(source->Shutdown()), "Shutdown");
            Check(source->Shutdown() == MF_E_SHUTDOWN, "second Shutdown -> MF_E_SHUTDOWN");
            activate->DetachObject();
        }
        Check(canUnload() == S_OK, "DllCanUnloadNow == S_OK after releasing everything");
        FreeLibrary(dll);
    }

    int CmdSelftest(int argc, wchar_t** argv)
    {
        std::wstring dll = DefaultDllPath();
        for (int i = 2; i < argc; i++)
        {
            if (std::wstring(argv[i]) == L"--dll" && i + 1 < argc)
                dll = argv[++i];
            else
                return std::wprintf(L"usage: vcam selftest [--dll PATH]\n"), 2;
        }
        PatternTests();
        ComScope com;
        if (FAILED(com.hr))
            return PrintHr(L"MFStartup", com.hr), 1;
        SourceTests(dll);
        std::printf("%s (%d failure(s))\n", g_failures ? "FAILED" : "PASSED", g_failures);
        return g_failures ? 1 : 0;
    }

    int Usage()
    {
        std::wprintf(
            L"usage:\n"
            L"  vcam status                        is the media source registered in HKLM?\n"
            L"  vcam add [--seconds N] [--grab F]  create \"%ls\" until N s pass or Enter\n"
            L"  vcam list                          list video capture devices\n"
            L"  vcam grab [--frames F] [--format nv12|yuy2] [--size WxH]\n"
            L"                                     read frames from the virtual camera and check them\n"
            L"  vcam selftest [--dll PATH]         pattern + in-process media source checks (no admin)\n",
            FriendlyName);
        return 2;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
        return Usage();
    const std::wstring command = argv[1];
    if (command == L"status")
        return IsRegistered(true) ? 0 : 1;
    if (command == L"add")
        return CmdAdd(argc, argv);
    if (command == L"list")
        return CmdList();
    if (command == L"grab")
        return CmdGrab(argc, argv);
    if (command == L"selftest")
        return CmdSelftest(argc, argv);
    return Usage();
}
