// The virtual camera's media source: one video stream offering NV12 and YUY2 at
// 1280x720 and 1920x1080, 60 fps, rendered on the CPU. Frame Server activates it through
// Activator (IMFActivate) and drives it like any MF device source.
#pragma once

#include "Common.h"
#include "Pattern.h"

#include <deque>
#include <mutex>
#include <thread>

namespace vcam
{
    using Microsoft::WRL::ChainInterfaces;
    using Microsoft::WRL::ComPtr;
    using Microsoft::WRL::FtmBase;
    using Microsoft::WRL::RuntimeClass;
    using Microsoft::WRL::RuntimeClassFlags;
    using Microsoft::WRL::ClassicCom;

    class MediaSource;

    class MediaStream : public RuntimeClass<RuntimeClassFlags<ClassicCom>,
        ChainInterfaces<IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator>,
        IMFAttributes, IKsControl, FtmBase>
    {
    public:
        MediaStream() = default;
        ~MediaStream() override;
        MediaStream(const MediaStream&) = delete;
        MediaStream& operator=(const MediaStream&) = delete;

        HRESULT RuntimeClassInitialize(IMFMediaSource* source, DWORD streamId);

        // IMFMediaEventGenerator
        STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
        STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
        STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
        STDMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* eventValue) override;

        // IMFMediaStream
        STDMETHODIMP GetMediaSource(IMFMediaSource** source) override;
        STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** descriptor) override;
        STDMETHODIMP RequestSample(IUnknown* token) override;

        // IMFMediaStream2
        STDMETHODIMP SetStreamState(MF_STREAM_STATE newState) override;
        STDMETHODIMP GetStreamState(MF_STREAM_STATE* current) override;

        // IKsControl: no properties exposed.
        STDMETHODIMP KsProperty(PKSPROPERTY property, ULONG propertyLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
        STDMETHODIMP KsMethod(PKSMETHOD method, ULONG methodLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
        STDMETHODIMP KsEvent(PKSEVENT event, ULONG eventLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;

        VCAM_FORWARD_IMFATTRIBUTES(attributes_)

        // Called by MediaSource.
        HRESULT Start(IMFMediaType* type);
        HRESULT Stop();
        void Shutdown();
        HRESULT SetAllocator(IUnknown* allocator);
        bool IsRunning();

    private:
        HRESULT StartLocked(IMFMediaType* type);
        HRESULT StopLocked();
        // Fills and queues one sample. Returns false if the allocator had no free sample.
        bool DeliverLocked(IUnknown* token);
        void PacerLoop();
        void StopPacer();

        std::mutex lock_;
        ComPtr<IMFAttributes> attributes_;
        ComPtr<IMFMediaEventQueue> queue_;
        ComPtr<IMFStreamDescriptor> descriptor_;
        ComPtr<IMFMediaSource> source_; // cleared on Shutdown (breaks the source <-> stream cycle)
        ComPtr<IMFVideoSampleAllocatorEx> allocator_;
        ComPtr<IMFMediaType> type_;
        MF_STREAM_STATE state_ = MF_STREAM_STATE_STOPPED;
        PatternCache pattern_;
        PixelFormat format_ = PixelFormat::NV12;
        uint32_t width_ = 0;
        uint32_t height_ = 0;
        uint64_t frameNumber_ = 0;
        LONGLONG nextDue_ = 0; // MFGetSystemTime units
        bool discontinuity_ = true;
        std::deque<ComPtr<IUnknown>> pending_; // RequestSample tokens waiting for their frame slot
        // Pacer thread: delivers one pending request per 1/60 s slot, timed by a high-resolution waitable timer.
        std::thread pacer_;
        HANDLE wake_ = nullptr;  // auto-reset event: new request, state change or exit
        HANDLE timer_ = nullptr; // high-resolution waitable timer
        bool exit_ = false;
        ObjectCounter counter_;
    };

    class MediaSource : public RuntimeClass<RuntimeClassFlags<ClassicCom>,
        ChainInterfaces<IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator>,
        IMFGetService, IKsControl, IMFSampleAllocatorControl, IMFAttributes, FtmBase>
    {
    public:
        MediaSource() = default;
        MediaSource(const MediaSource&) = delete;
        MediaSource& operator=(const MediaSource&) = delete;

        HRESULT RuntimeClassInitialize(IMFAttributes* activatorAttributes);

        // IMFMediaEventGenerator
        STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
        STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
        STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
        STDMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* eventValue) override;

        // IMFMediaSource
        STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor) override;
        STDMETHODIMP GetCharacteristics(DWORD* characteristics) override;
        STDMETHODIMP Pause() override;
        STDMETHODIMP Shutdown() override;
        STDMETHODIMP Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition) override;
        STDMETHODIMP Stop() override;

        // IMFMediaSourceEx
        STDMETHODIMP GetSourceAttributes(IMFAttributes** attributes) override;
        STDMETHODIMP GetStreamAttributes(DWORD streamId, IMFAttributes** attributes) override;
        STDMETHODIMP SetD3DManager(IUnknown* manager) override;

        // IMFGetService
        STDMETHODIMP GetService(REFGUID service, REFIID riid, LPVOID* object) override;

        // IMFSampleAllocatorControl
        STDMETHODIMP SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator) override;
        STDMETHODIMP GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage) override;

        // IKsControl: no properties exposed.
        STDMETHODIMP KsProperty(PKSPROPERTY property, ULONG propertyLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
        STDMETHODIMP KsMethod(PKSMETHOD method, ULONG methodLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
        STDMETHODIMP KsEvent(PKSEVENT event, ULONG eventLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;

        VCAM_FORWARD_IMFATTRIBUTES(attributes_)

    private:
        static constexpr DWORD StreamId = 0;

        std::mutex lock_;
        bool shutdown_ = false;
        ComPtr<IMFAttributes> attributes_;
        ComPtr<IMFMediaEventQueue> queue_;
        ComPtr<IMFPresentationDescriptor> descriptor_;
        ComPtr<MediaStream> stream_;
        ObjectCounter counter_;
    };

    // Builds the stream's media types (NV12/YUY2 x 720p/1080p, 60 fps, BT.709 limited range).
    HRESULT CreateMediaTypes(IMFMediaType* types[4]);
}
