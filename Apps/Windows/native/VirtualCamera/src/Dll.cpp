// COM entry points of HDMICaptainVCam.dll. The class object is an IMFActivate that
// MFCreateVirtualCamera's Frame Server activates; it hands out the MediaSource.
#include "MediaSource.h"

#include <string>

namespace vcam
{
    std::atomic<long> g_objects{ 0 };

    namespace
    {
        HMODULE g_module = nullptr;

        class Activator : public RuntimeClass<RuntimeClassFlags<ClassicCom>,
            ChainInterfaces<IMFActivate, IMFAttributes>, FtmBase>
        {
        public:
            Activator() = default;
            Activator(const Activator&) = delete;
            Activator& operator=(const Activator&) = delete;

            HRESULT RuntimeClassInitialize()
            {
                VCAM_RETURN_IF_FAILED(MFCreateAttributes(&attributes_, 4));
                VCAM_RETURN_IF_FAILED(attributes_->SetUINT32(MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES, 1));
                VCAM_RETURN_IF_FAILED(attributes_->SetGUID(MFT_TRANSFORM_CLSID_Attribute, CLSID_HDMICaptainVCam));
                return S_OK;
            }

            // IMFActivate
            STDMETHODIMP ActivateObject(REFIID riid, void** object) override
            {
                if (!object)
                    return E_POINTER;
                *object = nullptr;
                std::lock_guard guard(lock_);
                if (!source_)
                    VCAM_RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<MediaSource>(&source_, attributes_.Get()));
                return source_->QueryInterface(riid, object);
            }

            // The consumer shuts the source down itself; shutting it down here too would make
            // Frame Server see a second Shutdown and block removal (see VCamSample's notes).
            STDMETHODIMP ShutdownObject() override { return S_OK; }

            STDMETHODIMP DetachObject() override
            {
                std::lock_guard guard(lock_);
                source_.Reset();
                return S_OK;
            }

            VCAM_FORWARD_IMFATTRIBUTES(attributes_)

        private:
            std::mutex lock_;
            ComPtr<IMFAttributes> attributes_;
            ComPtr<MediaSource> source_;
            ObjectCounter counter_;
        };

        class ClassFactory : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IClassFactory, FtmBase>
        {
        public:
            STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** object) override
            {
                if (!object)
                    return E_POINTER;
                *object = nullptr;
                if (outer)
                    return CLASS_E_NOAGGREGATION;
                ComPtr<Activator> activator;
                VCAM_RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<Activator>(&activator));
                return activator->QueryInterface(riid, object);
            }

            STDMETHODIMP LockServer(BOOL lock) override
            {
                if (lock)
                    g_objects++;
                else
                    g_objects--;
                return S_OK;
            }

        private:
            ObjectCounter counter_;
        };

        std::wstring ModulePath()
        {
            std::wstring path(MAX_PATH, L'\0');
            for (;;)
            {
                const DWORD n = GetModuleFileNameW(g_module, path.data(), static_cast<DWORD>(path.size()));
                if (n == 0)
                    return {};
                if (n < path.size())
                {
                    path.resize(n);
                    return path;
                }
                path.resize(path.size() * 2);
            }
        }

        LSTATUS SetString(HKEY key, const wchar_t* name, const std::wstring& value)
        {
            return RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()),
                static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
        }

        const std::wstring ClsidKey = std::wstring(L"Software\\Classes\\CLSID\\") + CLSID_HDMICaptainVCamString;
    }
}

using namespace vcam;

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

_Check_return_ STDAPI DllGetClassObject(_In_ REFCLSID clsid, _In_ REFIID riid, _Outptr_ LPVOID* object)
{
    if (!object)
        return E_POINTER;
    *object = nullptr;
    if (clsid != CLSID_HDMICaptainVCam)
        return CLASS_E_CLASSNOTAVAILABLE;
    ComPtr<ClassFactory> factory;
    VCAM_RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<ClassFactory>(&factory));
    return factory->QueryInterface(riid, object);
}

__control_entrypoint(DllExport) STDAPI DllCanUnloadNow()
{
    return g_objects.load() == 0 ? S_OK : S_FALSE;
}

// Frame Server runs as LocalService and only sees HKLM, so the server must be registered
// there (elevated regsvr32). HKCU registrations are invisible to it.
STDAPI DllRegisterServer()
{
    const std::wstring path = ModulePath();
    if (path.empty())
        return HRESULT_FROM_WIN32(GetLastError());

    HKEY clsidKey = nullptr;
    LSTATUS status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, ClsidKey.c_str(), 0, nullptr, 0, KEY_WRITE | KEY_WOW64_64KEY, nullptr, &clsidKey, nullptr);
    if (status != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(status);
    status = SetString(clsidKey, nullptr, FriendlyName);
    HKEY inproc = nullptr;
    if (status == ERROR_SUCCESS)
        status = RegCreateKeyExW(clsidKey, L"InprocServer32", 0, nullptr, 0, KEY_WRITE | KEY_WOW64_64KEY, nullptr, &inproc, nullptr);
    if (status == ERROR_SUCCESS)
    {
        status = SetString(inproc, nullptr, path);
        if (status == ERROR_SUCCESS)
            status = SetString(inproc, L"ThreadingModel", L"Both");
        RegCloseKey(inproc);
    }
    RegCloseKey(clsidKey);
    return HRESULT_FROM_WIN32(status);
}

STDAPI DllUnregisterServer()
{
    const LSTATUS status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, ClsidKey.c_str());
    return status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(status);
}
