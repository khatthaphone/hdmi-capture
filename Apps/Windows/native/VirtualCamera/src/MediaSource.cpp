#include "MediaSource.h"

#include <propvarutil.h>

namespace vcam
{
    HRESULT MediaSource::RuntimeClassInitialize(IMFAttributes* activatorAttributes)
    {
        VCAM_RETURN_IF_FAILED(MFCreateAttributes(&attributes_, 8));
        if (activatorAttributes)
            VCAM_RETURN_IF_FAILED(activatorAttributes->CopyAllItems(attributes_.Get()));

        // Sensor profiles (same filter syntax as Microsoft's and VCamSample's sources):
        // every type we offer is 60 fps, so it fits both the legacy and high-frame-rate profiles.
        ComPtr<IMFSensorProfileCollection> profiles;
        VCAM_RETURN_IF_FAILED(MFCreateSensorProfileCollection(&profiles));
        ComPtr<IMFSensorProfile> profile;
        VCAM_RETURN_IF_FAILED(MFCreateSensorProfile(KSCAMERAPROFILE_Legacy, 0, nullptr, &profile));
        VCAM_RETURN_IF_FAILED(profile->AddProfileFilter(StreamId, L"((RES==;FRT<=60,1;SUT==))"));
        VCAM_RETURN_IF_FAILED(profiles->AddProfile(profile.Get()));
        profile.Reset();
        VCAM_RETURN_IF_FAILED(MFCreateSensorProfile(KSCAMERAPROFILE_HighFrameRate, 0, nullptr, &profile));
        VCAM_RETURN_IF_FAILED(profile->AddProfileFilter(StreamId, L"((RES==;FRT>=60,1;SUT==))"));
        VCAM_RETURN_IF_FAILED(profiles->AddProfile(profile.Get()));
        VCAM_RETURN_IF_FAILED(attributes_->SetUnknown(MF_DEVICEMFT_SENSORPROFILE_COLLECTION, profiles.Get()));

        VCAM_RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<MediaStream>(&stream_, static_cast<IMFMediaSource*>(this), StreamId));
        ComPtr<IMFStreamDescriptor> streamDescriptor;
        VCAM_RETURN_IF_FAILED(stream_->GetStreamDescriptor(&streamDescriptor));
        IMFStreamDescriptor* descriptors[1] = { streamDescriptor.Get() };
        VCAM_RETURN_IF_FAILED(MFCreatePresentationDescriptor(1, descriptors, &descriptor_));
        VCAM_RETURN_IF_FAILED(descriptor_->SelectStream(0));
        VCAM_RETURN_IF_FAILED(MFCreateEventQueue(&queue_));
        return S_OK;
    }

    // IMFMediaEventGenerator. The queue is used outside the lock: GetEvent can block.
    STDMETHODIMP MediaSource::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
    {
        ComPtr<IMFMediaEventQueue> queue;
        {
            std::lock_guard guard(lock_);
            queue = queue_;
        }
        return queue ? queue->BeginGetEvent(callback, state) : MF_E_SHUTDOWN;
    }

    STDMETHODIMP MediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
    {
        if (!event)
            return E_POINTER;
        *event = nullptr;
        ComPtr<IMFMediaEventQueue> queue;
        {
            std::lock_guard guard(lock_);
            queue = queue_;
        }
        return queue ? queue->EndGetEvent(result, event) : MF_E_SHUTDOWN;
    }

    STDMETHODIMP MediaSource::GetEvent(DWORD flags, IMFMediaEvent** event)
    {
        if (!event)
            return E_POINTER;
        *event = nullptr;
        ComPtr<IMFMediaEventQueue> queue;
        {
            std::lock_guard guard(lock_);
            queue = queue_;
        }
        return queue ? queue->GetEvent(flags, event) : MF_E_SHUTDOWN;
    }

    STDMETHODIMP MediaSource::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* eventValue)
    {
        std::lock_guard guard(lock_);
        return queue_ ? queue_->QueueEventParamVar(type, extendedType, status, eventValue) : MF_E_SHUTDOWN;
    }

    // IMFMediaSource
    STDMETHODIMP MediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor)
    {
        if (!descriptor)
            return E_POINTER;
        *descriptor = nullptr;
        std::lock_guard guard(lock_);
        return shutdown_ ? MF_E_SHUTDOWN : descriptor_->Clone(descriptor);
    }

    STDMETHODIMP MediaSource::GetCharacteristics(DWORD* characteristics)
    {
        if (!characteristics)
            return E_POINTER;
        std::lock_guard guard(lock_);
        if (shutdown_)
            return MF_E_SHUTDOWN;
        *characteristics = MFMEDIASOURCE_IS_LIVE;
        return S_OK;
    }

    STDMETHODIMP MediaSource::Pause()
    {
        std::lock_guard guard(lock_);
        return shutdown_ ? MF_E_SHUTDOWN : MF_E_INVALID_STATE_TRANSITION;
    }

    STDMETHODIMP MediaSource::Shutdown()
    {
        std::lock_guard guard(lock_);
        if (shutdown_)
            return MF_E_SHUTDOWN;
        shutdown_ = true;
        queue_->Shutdown();
        stream_->Shutdown();
        stream_.Reset();
        descriptor_.Reset();
        queue_.Reset();
        return S_OK;
    }

    STDMETHODIMP MediaSource::Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition)
    {
        if (!descriptor || !startPosition)
            return E_INVALIDARG;
        if (timeFormat && *timeFormat != GUID_NULL)
            return MF_E_UNSUPPORTED_TIME_FORMAT;
        std::lock_guard guard(lock_);
        if (shutdown_)
            return MF_E_SHUTDOWN;

        DWORD count = 0;
        VCAM_RETURN_IF_FAILED(descriptor->GetStreamDescriptorCount(&count));
        for (DWORD i = 0; i < count; i++)
        {
            BOOL selected = FALSE;
            ComPtr<IMFStreamDescriptor> streamDescriptor;
            VCAM_RETURN_IF_FAILED(descriptor->GetStreamDescriptorByIndex(i, &selected, &streamDescriptor));
            DWORD id = 0;
            VCAM_RETURN_IF_FAILED(streamDescriptor->GetStreamIdentifier(&id));
            if (id != StreamId)
                return MF_E_INVALIDSTREAMNUMBER;

            const bool running = stream_->IsRunning();
            if (selected)
            {
                ComPtr<IMFMediaTypeHandler> handler;
                ComPtr<IMFMediaType> type;
                VCAM_RETURN_IF_FAILED(streamDescriptor->GetMediaTypeHandler(&handler));
                VCAM_RETURN_IF_FAILED(handler->GetCurrentMediaType(&type));
                VCAM_RETURN_IF_FAILED(descriptor_->SelectStream(0));
                ComPtr<IUnknown> unknown;
                VCAM_RETURN_IF_FAILED(stream_.As(&unknown));
                VCAM_RETURN_IF_FAILED(queue_->QueueEventParamUnk(running ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK, unknown.Get()));
                VCAM_RETURN_IF_FAILED(stream_->Start(type.Get()));
            }
            else if (running)
            {
                VCAM_RETURN_IF_FAILED(stream_->Stop());
                VCAM_RETURN_IF_FAILED(descriptor_->DeselectStream(0));
            }
        }

        PROPVARIANT time;
        VCAM_RETURN_IF_FAILED(InitPropVariantFromInt64(MFGetSystemTime(), &time));
        const HRESULT hr = queue_->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &time);
        PropVariantClear(&time);
        return hr;
    }

    STDMETHODIMP MediaSource::Stop()
    {
        std::lock_guard guard(lock_);
        if (shutdown_)
            return MF_E_SHUTDOWN;
        if (stream_->IsRunning())
        {
            VCAM_RETURN_IF_FAILED(stream_->Stop());
            VCAM_RETURN_IF_FAILED(descriptor_->DeselectStream(0));
        }
        PROPVARIANT time;
        VCAM_RETURN_IF_FAILED(InitPropVariantFromInt64(MFGetSystemTime(), &time));
        const HRESULT hr = queue_->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, &time);
        PropVariantClear(&time);
        return hr;
    }

    // IMFMediaSourceEx
    STDMETHODIMP MediaSource::GetSourceAttributes(IMFAttributes** attributes)
    {
        if (!attributes)
            return E_POINTER;
        *attributes = nullptr;
        std::lock_guard guard(lock_);
        return shutdown_ ? MF_E_SHUTDOWN : attributes_.CopyTo(attributes);
    }

    STDMETHODIMP MediaSource::GetStreamAttributes(DWORD streamId, IMFAttributes** attributes)
    {
        if (!attributes)
            return E_POINTER;
        *attributes = nullptr;
        std::lock_guard guard(lock_);
        if (shutdown_)
            return MF_E_SHUTDOWN;
        if (streamId != StreamId)
            return MF_E_INVALIDSTREAMNUMBER;
        return stream_.CopyTo(attributes);
    }

    STDMETHODIMP MediaSource::SetD3DManager(IUnknown*)
    {
        // Frames are rendered on the CPU into whatever buffers the allocator hands out.
        std::lock_guard guard(lock_);
        return shutdown_ ? MF_E_SHUTDOWN : S_OK;
    }

    // IMFGetService
    STDMETHODIMP MediaSource::GetService(REFGUID, REFIID, LPVOID* object)
    {
        if (object)
            *object = nullptr;
        return MF_E_UNSUPPORTED_SERVICE;
    }

    // IMFSampleAllocatorControl
    STDMETHODIMP MediaSource::SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator)
    {
        std::lock_guard guard(lock_);
        if (shutdown_)
            return MF_E_SHUTDOWN;
        if (outputStreamId != StreamId)
            return MF_E_INVALIDSTREAMNUMBER;
        return stream_->SetAllocator(allocator);
    }

    STDMETHODIMP MediaSource::GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage)
    {
        if (!inputStreamId || !usage)
            return E_POINTER;
        std::lock_guard guard(lock_);
        if (shutdown_)
            return MF_E_SHUTDOWN;
        if (outputStreamId != StreamId)
            return MF_E_INVALIDSTREAMNUMBER;
        *inputStreamId = outputStreamId;
        *usage = MFSampleAllocatorUsage_UsesProvidedAllocator;
        return S_OK;
    }

    // IKsControl
    STDMETHODIMP MediaSource::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG* bytesReturned)
    {
        if (bytesReturned)
            *bytesReturned = 0;
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    STDMETHODIMP MediaSource::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG* bytesReturned)
    {
        if (bytesReturned)
            *bytesReturned = 0;
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    STDMETHODIMP MediaSource::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG* bytesReturned)
    {
        if (bytesReturned)
            *bytesReturned = 0;
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }
}
