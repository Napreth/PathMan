#pragma once
#include <array>
#include <stdexcept>
#include <string_view>

namespace pathman_version {
inline std::array<unsigned,3> parse(std::wstring_view value) {
    std::array<unsigned,3> result{};
    for(size_t index=0;index<result.size();++index) {
        const auto end=value.find(L'.');
        const auto part=value.substr(0,end);
        if(part.empty() || (part.size()>1 && part.front()==L'0') || (index<2)==(end==std::wstring_view::npos))
            throw std::invalid_argument("Invalid X.Y.Z build number");
        for(const auto digit:part) {
            if(digit<L'0' || digit>L'9' || result[index]>6553)throw std::invalid_argument("Invalid build component");
            result[index]=result[index]*10+static_cast<unsigned>(digit-L'0');
            if(result[index]>65535)throw std::invalid_argument("Build component exceeds 65535");
        }
        if(end!=std::wstring_view::npos)value.remove_prefix(end+1);
    }
    return result;
}
inline int compare(std::wstring_view left,std::wstring_view right) {
    const auto a=parse(left),b=parse(right);
    return a==b ? 0 : a>b ? 1 : -1;
}
}
