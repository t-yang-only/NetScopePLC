#define _WIN32_WINNT 0x0601
#define NTDDI_VERSION 0x06010000
#define WINAPI_FAMILY WINAPI_FAMILY_DESKTOP_APP
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <iphlpapi.h>
#include <icmpapi.h>
#include <ws2tcpip.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>

#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "ws2_32.lib")

typedef struct {
    IPAddr source;
    NET_IFINDEX interface_index;
    uint32_t network;
    uint64_t hosts;
    volatile LONG64 next;
    volatile LONG64 scanned;
    volatile LONG64 replied;
    CRITICAL_SECTION output_lock;
} SCAN_CONTEXT;

static void output_wide(const wchar_t *text);
static void ip_to_text(IPAddr address, wchar_t *buffer, size_t size);

static NET_IFINDEX interface_for_source(IPAddr source)
{
    ULONG length = 0;
    if (GetAdaptersAddresses(AF_INET, 0, NULL, NULL, &length) != ERROR_BUFFER_OVERFLOW)
        return 0;
    IP_ADAPTER_ADDRESSES *all = (IP_ADAPTER_ADDRESSES *)malloc(length);
    if (!all || GetAdaptersAddresses(AF_INET, 0, NULL, all, &length) != NO_ERROR) {
        free(all); return 0;
    }
    NET_IFINDEX result = 0;
    for (IP_ADAPTER_ADDRESSES *adapter = all; adapter && !result; adapter = adapter->Next)
        for (IP_ADAPTER_UNICAST_ADDRESS *item = adapter->FirstUnicastAddress; item; item = item->Next)
            if (item->Address.lpSockaddr->sa_family == AF_INET &&
                ((SOCKADDR_IN *)item->Address.lpSockaddr)->sin_addr.S_un.S_addr == source) {
                result = adapter->IfIndex; break;
            }
    free(all);
    return result;
}

static void output_arp_hosts(SCAN_CONTEXT *context)
{
    ULONG size = 0;
    GetIpNetTable(NULL, &size, FALSE);
    PMIB_IPNETTABLE table = (PMIB_IPNETTABLE)malloc(size);
    if (!context->interface_index || !table || GetIpNetTable(table, &size, FALSE) != NO_ERROR) {
        free(table); return;
    }
    for (DWORD i = 0; i < table->dwNumEntries; ++i) {
        MIB_IPNETROW *row = &table->table[i];
        uint32_t host = ntohl(row->dwAddr);
        uint64_t offset = host >= context->network ? (uint64_t)(host - context->network) : context->hosts;
        if (row->dwIndex != context->interface_index || offset >= context->hosts ||
            row->dwPhysAddrLen == 0 || row->dwType == MIB_IPNET_TYPE_INVALID)
            continue;
        wchar_t address[INET_ADDRSTRLEN], mac[64] = L"", line[160];
        ip_to_text(row->dwAddr, address, _countof(address));
        for (DWORD b = 0; b < row->dwPhysAddrLen; ++b) {
            wchar_t part[5];
            _snwprintf_s(part, _countof(part), _TRUNCATE, b ? L"-%02X" : L"%02X", row->bPhysAddr[b]);
            wcscat_s(mac, _countof(mac), part);
        }
        _snwprintf_s(line, _countof(line), _TRUNCATE, L"ARP\t%s\t%s\n", address, mac);
        output_wide(line);
    }
    free(table);
}

static void output_wide(const wchar_t *text)
{
    int length = WideCharToMultiByte(CP_UTF8, 0, text, -1, NULL, 0, NULL, NULL);
    char *utf8 = (char *)malloc((size_t)length);
    DWORD written;
    HANDLE stdout_handle = GetStdHandle(STD_OUTPUT_HANDLE);

    if (!utf8)
        return;
    WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, length, NULL, NULL);
    WriteFile(stdout_handle, utf8, (DWORD)(length - 1), &written, NULL);
    free(utf8);
}

static void ip_to_text(IPAddr address, wchar_t *buffer, size_t size)
{
    IN_ADDR value;
    value.S_un.S_addr = address;
    InetNtopW(AF_INET, &value, buffer, (DWORD)size);
}

static int is_usable_ipv4(const SOCKADDR *address)
{
    const SOCKADDR_IN *ipv4 = (const SOCKADDR_IN *)address;
    return address && address->sa_family == AF_INET && ipv4->sin_addr.S_un.S_addr != 0;
}

static void list_adapters(void)
{
    ULONG length = 0;
    ULONG flags = GAA_FLAG_INCLUDE_GATEWAYS;
    DWORD result = GetAdaptersAddresses(AF_INET, flags, NULL, NULL, &length);
    IP_ADAPTER_ADDRESSES *addresses;
    IP_ADAPTER_ADDRESSES *adapter;

    if (result != ERROR_BUFFER_OVERFLOW) {
        fwprintf(stderr, L"ERROR\tGetAdaptersAddresses failed: %lu\n", result);
        return;
    }
    addresses = (IP_ADAPTER_ADDRESSES *)malloc(length);
    if (!addresses) {
        fputs("ERROR\tout of memory\n", stderr);
        return;
    }
    result = GetAdaptersAddresses(AF_INET, flags, NULL, addresses, &length);
    if (result != NO_ERROR) {
        fwprintf(stderr, L"ERROR\tGetAdaptersAddresses failed: %lu\n", result);
        free(addresses);
        return;
    }

    for (adapter = addresses; adapter; adapter = adapter->Next) {
        IP_ADAPTER_UNICAST_ADDRESS *unicast;
        int emitted = 0;
        wchar_t gateway[INET_ADDRSTRLEN] = L"";
        IP_ADAPTER_GATEWAY_ADDRESS_LH *gateway_address;

        if (adapter->IfType == IF_TYPE_SOFTWARE_LOOPBACK)
            continue;
        for (gateway_address = adapter->FirstGatewayAddress;
             gateway_address;
             gateway_address = gateway_address->Next) {
            if (is_usable_ipv4(gateway_address->Address.lpSockaddr)) {
                ip_to_text(((SOCKADDR_IN *)gateway_address->Address.lpSockaddr)->sin_addr.S_un.S_addr,
                           gateway, _countof(gateway));
                break;
            }
        }
        for (unicast = adapter->FirstUnicastAddress; unicast; unicast = unicast->Next) {
            wchar_t address[INET_ADDRSTRLEN];
            wchar_t line[1024];
            if (!is_usable_ipv4(unicast->Address.lpSockaddr))
                continue;
            ip_to_text(((SOCKADDR_IN *)unicast->Address.lpSockaddr)->sin_addr.S_un.S_addr,
                       address, _countof(address));
            _snwprintf_s(line, _countof(line), _TRUNCATE, L"%S\t%s\t%s\t%u\t%s\n",
                         adapter->AdapterName,
                         adapter->FriendlyName ? adapter->FriendlyName : L"",
                         address, unicast->OnLinkPrefixLength, gateway);
            output_wide(line);
            emitted = 1;
        }
        if (!emitted) {
            wchar_t line[1024];
            _snwprintf_s(line, _countof(line), _TRUNCATE, L"%S\t%s\t0.0.0.0\t0\t\n",
                         adapter->AdapterName,
                         adapter->FriendlyName ? adapter->FriendlyName : L"");
            output_wide(line);
        }
    }
    free(addresses);
}

static DWORD WINAPI scan_worker(void *argument)
{
    SCAN_CONTEXT *context = (SCAN_CONTEXT *)argument;
    HANDLE icmp = IcmpCreateFile();
    char reply[sizeof(ICMP_ECHO_REPLY) + 64];

    if (icmp == INVALID_HANDLE_VALUE)
        return 0;
    for (;;) {
        LONG64 offset = InterlockedIncrement64(&context->next) - 1;
        uint32_t target_number;
        IPAddr target;
        DWORD count;
        if ((uint64_t)offset >= context->hosts)
            break;
        target_number = context->network + (uint32_t)offset;
        target = htonl(target_number);
        if (target == context->source)
            continue;
        count = IcmpSendEcho2Ex(icmp, NULL, NULL, NULL, context->source, target,
                                "PLC", 3, NULL, reply, sizeof(reply), 130);
        InterlockedIncrement64(&context->scanned);
        if (count) {
            ICMP_ECHO_REPLY *echo = (ICMP_ECHO_REPLY *)reply;
            wchar_t address[INET_ADDRSTRLEN];
            wchar_t line[96];
            ip_to_text(target, address, _countof(address));
            _snwprintf_s(line, _countof(line), _TRUNCATE, L"HOST\t%s\t%lu\n",
                         address, echo->RoundTripTime);
            EnterCriticalSection(&context->output_lock);
            output_wide(line);
            LeaveCriticalSection(&context->output_lock);
            InterlockedIncrement64(&context->replied);
        }
    }
    IcmpCloseHandle(icmp);
    return 0;
}

static int parse_prefix(const wchar_t *value, unsigned int *prefix)
{
    wchar_t *end;
    unsigned long parsed = wcstoul(value, &end, 10);
    if (*value == L'\0' || *end != L'\0' || parsed > 30)
        return 0;
    *prefix = (unsigned int)parsed;
    return 1;
}

static void scan_network(const wchar_t *source_text, const wchar_t *prefix_text)
{
    IN_ADDR source_address;
    uint32_t source;
    uint32_t mask;
    unsigned int prefix;
    SYSTEM_INFO system_info;
    DWORD thread_count;
    HANDLE *threads;
    SCAN_CONTEXT context;
    wchar_t line[128];

    if (InetPtonW(AF_INET, source_text, &source_address) != 1 || !parse_prefix(prefix_text, &prefix)) {
        fputs("ERROR\tusage: --scan <IPv4> <prefix 0..30>\n", stderr);
        return;
    }
    source = ntohl(source_address.S_un.S_addr);
    mask = prefix ? 0xffffffffu << (32 - prefix) : 0;
    ZeroMemory(&context, sizeof(context));
    context.source = source_address.S_un.S_addr;
    context.interface_index = interface_for_source(context.source);
    context.network = source & mask;
    context.hosts = (UINT64_C(1) << (32 - prefix));
    if (prefix <= 30) {
        context.network++;
        context.hosts -= 2;
    }
    InitializeCriticalSection(&context.output_lock);
    GetSystemInfo(&system_info);
    thread_count = system_info.dwNumberOfProcessors * 8;
    if (thread_count < 16) thread_count = 16;
    if (thread_count > 64) thread_count = 64;
    if ((uint64_t)thread_count > context.hosts) thread_count = (DWORD)context.hosts;
    threads = (HANDLE *)calloc(thread_count, sizeof(HANDLE));
    if (!threads) {
        DeleteCriticalSection(&context.output_lock);
        fputs("ERROR\tout of memory\n", stderr);
        return;
    }
    for (DWORD index = 0; index < thread_count; ++index)
        threads[index] = CreateThread(NULL, 0, scan_worker, &context, 0, NULL);
    WaitForMultipleObjects(thread_count, threads, TRUE, INFINITE);
    for (DWORD index = 0; index < thread_count; ++index)
        CloseHandle(threads[index]);
    output_arp_hosts(&context);
    _snwprintf_s(line, _countof(line), _TRUNCATE, L"DONE\t%lld\t%lld\n",
                 context.scanned, context.replied);
    output_wide(line);
    free(threads);
    DeleteCriticalSection(&context.output_lock);
}

int wmain(int argc, wchar_t **argv)
{
    WSADATA winsock;
    if (WSAStartup(MAKEWORD(2, 2), &winsock) != 0)
        return 1;
    if (argc == 2 && wcscmp(argv[1], L"--adapters") == 0)
        list_adapters();
    else if (argc == 4 && wcscmp(argv[1], L"--scan") == 0)
        scan_network(argv[2], argv[3]);
    else
        fputs("usage: netscope_native --adapters | --scan <IPv4> <prefix>\n", stderr);
    WSACleanup();
    return 0;
}
