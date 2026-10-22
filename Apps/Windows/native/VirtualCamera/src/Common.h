// Shared declarations for the HDMI Captain virtual camera DLL and the vcam tool.
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>
#include <unknwn.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfvirtualcamera.h>
#include <ks.h>
#include <ksproxy.h> // user-mode IKsControl
#include <ksmedia.h>
#include <wrl/client.h>
#include <wrl/implements.h>

#include <atomic>
#include <cstdint>

namespace vcam
{
    // {9D955755-73BA-4CA0-8142-38332817B22C}
    inline constexpr GUID CLSID_HDMICaptainVCam = { 0x9d955755, 0x73ba, 0x4ca0, { 0x81, 0x42, 0x38, 0x33, 0x28, 0x17, 0xb2, 0x2c } };
    inline constexpr wchar_t CLSID_HDMICaptainVCamString[] = L"{9D955755-73BA-4CA0-8142-38332817B22C}";
    inline constexpr wchar_t FriendlyName[] = L"HDMI Captain Test Camera";

    // Frame rate of every media type we offer.
    inline constexpr UINT32 FrameRate = 60;
    // 100 ns units per frame: 10^7 / 60, rounded.
    inline constexpr LONGLONG FrameDuration = (10'000'000LL + FrameRate / 2) / FrameRate;

    // Live COM objects + server locks, for DllCanUnloadNow.
    extern std::atomic<long> g_objects;

    struct ObjectCounter
    {
        ObjectCounter() { g_objects++; }
        ~ObjectCounter() { g_objects--; }
        ObjectCounter(const ObjectCounter&) = delete;
        ObjectCounter& operator=(const ObjectCounter&) = delete;
    };
}

// Implements every IMFAttributes method by forwarding to the IMFAttributes member `store`.
#define VCAM_FORWARD_IMFATTRIBUTES(store) \
    STDMETHODIMP GetItem(REFGUID k, PROPVARIANT* v) override { return store->GetItem(k, v); } \
    STDMETHODIMP GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t) override { return store->GetItemType(k, t); } \
    STDMETHODIMP CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r) override { return store->CompareItem(k, v, r); } \
    STDMETHODIMP Compare(IMFAttributes* a, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r) override { return store->Compare(a, m, r); } \
    STDMETHODIMP GetUINT32(REFGUID k, UINT32* v) override { return store->GetUINT32(k, v); } \
    STDMETHODIMP GetUINT64(REFGUID k, UINT64* v) override { return store->GetUINT64(k, v); } \
    STDMETHODIMP GetDouble(REFGUID k, double* v) override { return store->GetDouble(k, v); } \
    STDMETHODIMP GetGUID(REFGUID k, GUID* v) override { return store->GetGUID(k, v); } \
    STDMETHODIMP GetStringLength(REFGUID k, UINT32* n) override { return store->GetStringLength(k, n); } \
    STDMETHODIMP GetString(REFGUID k, LPWSTR s, UINT32 c, UINT32* n) override { return store->GetString(k, s, c, n); } \
    STDMETHODIMP GetAllocatedString(REFGUID k, LPWSTR* s, UINT32* n) override { return store->GetAllocatedString(k, s, n); } \
    STDMETHODIMP GetBlobSize(REFGUID k, UINT32* n) override { return store->GetBlobSize(k, n); } \
    STDMETHODIMP GetBlob(REFGUID k, UINT8* b, UINT32 c, UINT32* n) override { return store->GetBlob(k, b, c, n); } \
    STDMETHODIMP GetAllocatedBlob(REFGUID k, UINT8** b, UINT32* n) override { return store->GetAllocatedBlob(k, b, n); } \
    STDMETHODIMP GetUnknown(REFGUID k, REFIID r, LPVOID* p) override { return store->GetUnknown(k, r, p); } \
    STDMETHODIMP SetItem(REFGUID k, REFPROPVARIANT v) override { return store->SetItem(k, v); } \
    STDMETHODIMP DeleteItem(REFGUID k) override { return store->DeleteItem(k); } \
    STDMETHODIMP DeleteAllItems() override { return store->DeleteAllItems(); } \
    STDMETHODIMP SetUINT32(REFGUID k, UINT32 v) override { return store->SetUINT32(k, v); } \
    STDMETHODIMP SetUINT64(REFGUID k, UINT64 v) override { return store->SetUINT64(k, v); } \
    STDMETHODIMP SetDouble(REFGUID k, double v) override { return store->SetDouble(k, v); } \
    STDMETHODIMP SetGUID(REFGUID k, REFGUID v) override { return store->SetGUID(k, v); } \
    STDMETHODIMP SetString(REFGUID k, LPCWSTR v) override { return store->SetString(k, v); } \
    STDMETHODIMP SetBlob(REFGUID k, const UINT8* b, UINT32 c) override { return store->SetBlob(k, b, c); } \
    STDMETHODIMP SetUnknown(REFGUID k, IUnknown* u) override { return store->SetUnknown(k, u); } \
    STDMETHODIMP LockStore() override { return store->LockStore(); } \
    STDMETHODIMP UnlockStore() override { return store->UnlockStore(); } \
    STDMETHODIMP GetCount(UINT32* n) override { return store->GetCount(n); } \
    STDMETHODIMP GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v) override { return store->GetItemByIndex(i, k, v); } \
    STDMETHODIMP CopyAllItems(IMFAttributes* d) override { return store->CopyAllItems(d); }

#define VCAM_RETURN_IF_FAILED(expr) \
    do { const HRESULT hr_ = (expr); if (FAILED(hr_)) return hr_; } while (0)
