#pragma once
#include <string>
#include <vector>
#include <algorithm>

namespace pathman_version {
inline std::vector<std::wstring> split(const std::wstring& value) {
    std::vector<std::wstring> result;size_t start=0;
    for(;;) {auto end=value.find(L'.',start);result.push_back(value.substr(start,end-start));if(end==std::wstring::npos) break;start=end+1;}
    return result;
}
inline bool numeric(const std::wstring& value) {return !value.empty() && std::all_of(value.begin(),value.end(),[](wchar_t c) {return c>=L'0' && c<=L'9';});}
inline int number(std::wstring left,std::wstring right) {
    auto trim=[](std::wstring value) {auto start=value.find_first_not_of(L'0');return start==std::wstring::npos ? std::wstring(L"0") : value.substr(start);};
    left=trim(left);right=trim(right);
    return left.size()!=right.size() ? (left.size()>right.size() ? 1 : -1) : left.compare(right);
}
inline int compare(std::wstring left,std::wstring right) {
    left=left.substr(0,left.find(L'+'));right=right.substr(0,right.find(L'+'));
    auto a=left.find(L'-');auto b=right.find(L'-');
    auto coreA=split(left.substr(0,a));auto coreB=split(right.substr(0,b));
    for(size_t i=0;i<3;i++) {auto result=number(i<coreA.size()?coreA[i]:L"0",i<coreB.size()?coreB[i]:L"0");if(result) return result;}
    if(a==std::wstring::npos || b==std::wstring::npos) return a==b ? 0 : a==std::wstring::npos ? 1 : -1;
    auto preA=split(left.substr(a+1));auto preB=split(right.substr(b+1));
    for(size_t i=0;i<std::min(preA.size(),preB.size());i++) {
        bool an=numeric(preA[i]),bn=numeric(preB[i]);
        auto result=an && bn ? number(preA[i],preB[i]) : an!=bn ? (an?-1:1) : preA[i].compare(preB[i]);if(result) return result;
    }
    return preA.size()==preB.size() ? 0 : preA.size()>preB.size() ? 1 : -1;
}
}
